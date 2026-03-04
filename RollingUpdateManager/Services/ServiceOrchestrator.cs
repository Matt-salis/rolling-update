using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RollingUpdateManager.Models;
using RollingUpdateManager.Proxy;

namespace RollingUpdateManager.Services
{
    /// <summary>
    /// Núcleo de la aplicación.
    /// Orquesta el ciclo de vida completo Blue/Green:
    ///   • Start / Stop / Restart servicio
    ///   • Rolling update sin downtime
    ///   • Expone eventos de log y cambio de estado para la UI
    /// </summary>
    public class ServiceOrchestrator : IAsyncDisposable
    {
        // ── Dependencias ───────────────────────────────────────────────────────
        private readonly PersistenceService _persistence;
        private readonly PortManager        _portManager;
        private readonly ProcessLauncher    _launcher;
        private readonly HealthCheckService _healthCheck;
        private readonly ProxyManager       _proxy;

        // Estado en memoria de todos los servicios registrados
        private readonly ConcurrentDictionary<string, ServiceRuntimeState> _states = new();

        // Mutex por servicio: evita que Start/Stop/Update corran en paralelo para el mismo servicio
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _serviceLocks = new();

        // Buffer de logs por servicio (ring-buffer de 2000 entradas)
        // Permite que la UI reciba logs aunque el VM se cree después del arranque
        private readonly ConcurrentDictionary<string, ConcurrentQueue<LogEntry>> _logBuffers = new();
        private const int LogBufferMaxSize = 2000;

        // Watchdog: detecta procesos que murieron sin ser notificados por StopAsync
        private readonly CancellationTokenSource _watchdogCts = new();
        private Task? _watchdogTask;

        // ── Eventos para la UI ─────────────────────────────────────────────────
        public event Action<LogEntry>?                 LogReceived;
        public event Action<ServiceRuntimeState>?      StateChanged;

        // ── Constructor ────────────────────────────────────────────────────────
        public ServiceOrchestrator(
            PersistenceService persistence,
            PortManager        portManager,
            ProcessLauncher    launcher,
            HealthCheckService healthCheck,
            ProxyManager       proxy)
        {
            _persistence = persistence;
            _portManager = portManager;
            _launcher    = launcher;
            _healthCheck = healthCheck;
            _proxy       = proxy;

            // Suscribir logs de los procesos hijos
            _launcher.LogReceived += OnChildLog;
        }

        // ── Inicialización: carga datos y arranca AutoStart ────────────────────
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            var data = await _persistence.LoadAsync(ct);
            _portManager.SetRange(data.PortRanges.RangeStart, data.PortRanges.RangeEnd);

            foreach (var config in data.Services)
            {
                var state = new ServiceRuntimeState { Config = config };
                _states[config.Id] = state;

                if (config.AutoStart)
                    _ = Task.Run(() => StartAsync(config.Id, ct), ct);
            }

            // Iniciar watchdog que detecta procesos muertos externamente
            _watchdogTask = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token));
        }

        // ── Registrar / actualizar configuración ───────────────────────────────
        public async Task AddOrUpdateServiceAsync(ServiceConfig config, CancellationToken ct = default)
        {
            if (!_states.ContainsKey(config.Id))
                _states[config.Id] = new ServiceRuntimeState { Config = config };
            else
                _states[config.Id].Config = config;

            await _persistence.UpsertServiceAsync(config, ct);
        }

        // ── Eliminar servicio ──────────────────────────────────────────────────
        public async Task RemoveServiceAsync(string serviceId, CancellationToken ct = default)
        {
            await StopAsync(serviceId, ct);
            await _proxy.RemoveProxyAsync(serviceId);
            _states.TryRemove(serviceId, out _);
            _portManager.ReleaseAllPortsFor(serviceId);
            await _persistence.RemoveServiceAsync(serviceId, ct);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  START
        // ═══════════════════════════════════════════════════════════════════════
        public async Task StartAsync(string serviceId, CancellationToken ct = default)
        {
            if (!_states.TryGetValue(serviceId, out var state)) return;

            var svcLock = _serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
            if (!await svcLock.WaitAsync(0, ct)) // no bloquear: si ya hay una op en curso, ignorar
            {
                Log(serviceId, "WARN", "Operación ya en curso para este servicio, ignorando solicitud duplicada.");
                return;
            }

            if (state.OverallStatus == ServiceStatus.Running)
            {
                svcLock.Release();
                Log(serviceId, "WARN", "El servicio ya está corriendo.");
                return;
            }

            state.OverallStatus = ServiceStatus.Starting;
            NotifyStateChange(state);

            var config = state.Config;
            var slot   = config.ActiveSlot;

            try
            {
                if (!File.Exists(config.JarPath))
                    throw new FileNotFoundException($"JAR no encontrado: {config.JarPath}");

                var instance = _launcher.Start(config, slot);

                // Asignar al slot correcto
                AssignSlot(state, slot, instance);

                var healthy = await _healthCheck.WaitForReadyAsync(
                    instance.InternalPort,
                    config.HealthCheckPath,
                    config.HealthCheckTimeoutSeconds,
                    config.HealthCheckIntervalSeconds,
                    instance.Process,
                    ct);

                if (!healthy)
                    throw new TimeoutException(
                        $"Health-check falló en {config.HealthCheckTimeoutSeconds}s");

                instance.Status     = ServiceStatus.Running;
                state.OverallStatus = ServiceStatus.Running;
                config.ActiveSlot   = slot;

                // Levantar proxy si no existe o actualizar target
                await _proxy.EnsureProxyAsync(
                    serviceId, config.PublicPort, instance.InternalPort, ct);

                Log(serviceId, "INFO",
                    $"Servicio iniciado correctamente en puerto público {config.PublicPort} " +
                    $"→ interno {instance.InternalPort} ({slot})");
            }
            catch (Exception ex)
            {
                // Limpiar la instancia fallida del slot para que el watchdog no la ignore
                var failedInst = GetSlotInstance(state, slot);
                if (failedInst is { Status: not ServiceStatus.Running })
                {
                    if (failedInst.Process is { HasExited: false })
                        try { failedInst.Process.Kill(entireProcessTree: true); } catch { }
                    AssignSlot(state, slot, null);
                }

                state.OverallStatus = ServiceStatus.Error;
                state.LastError     = ex.Message;
                Log(serviceId, "ERROR", $"Error al iniciar: {ex.Message}");
            }
            finally { svcLock.Release(); }

            NotifyStateChange(state);
            await _persistence.UpsertServiceAsync(config, ct);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  STOP
        // ═══════════════════════════════════════════════════════════════════════
        public async Task StopAsync(string serviceId, CancellationToken ct = default)
        {
            if (!_states.TryGetValue(serviceId, out var state)) return;

            Log(serviceId, "INFO", "Deteniendo servicio…");

            state.OverallStatus = ServiceStatus.Stopped;

            var tasks = new List<Task>();
            if (state.Blue  is { } b && b.Status != ServiceStatus.Stopped)
                tasks.Add(_launcher.StopAsync(b));
            if (state.Green is { } g && g.Status != ServiceStatus.Stopped)
                tasks.Add(_launcher.StopAsync(g));

            await Task.WhenAll(tasks);

            state.Blue  = null;
            state.Green = null;
            NotifyStateChange(state);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  RESTART
        // ═══════════════════════════════════════════════════════════════════════
        public async Task RestartAsync(string serviceId, CancellationToken ct = default)
        {
            await StopAsync(serviceId, ct);
            await Task.Delay(1000, ct); // pequeño respiro
            await StartAsync(serviceId, ct);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  ROLLING UPDATE (Blue/Green sin downtime)
        // ═══════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Despliega un nuevo JAR sin downtime:
        /// 1. Detecta el slot activo (ej: Blue).
        /// 2. Arranca el nuevo JAR en el slot standby (ej: Green).
        /// 3. Espera health-check OK.
        /// 4. Cambia el proxy al nuevo puerto interno.
        /// 5. Espera <see cref="ServiceConfig.DrainDelayMilliseconds"/> para drain.
        /// 6. Apaga el slot anterior (Blue).
        /// Si falla en cualquier paso, aborta y mantiene el slot activo intacto.
        /// </summary>
        public async Task RollingUpdateAsync(
            string serviceId,
            string newJarPath,
            CancellationToken ct = default)
        {
            if (!_states.TryGetValue(serviceId, out var state))
                throw new ArgumentException($"Servicio {serviceId} no encontrado.");

            var config = state.Config;

            // ── Validaciones ─────────────────────────────────────────────────────
            if (!File.Exists(newJarPath))
                throw new FileNotFoundException($"Nuevo JAR no encontrado: {newJarPath}");

            var svcLock = _serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
            if (!await svcLock.WaitAsync(0, ct))
                throw new InvalidOperationException("Ya hay una operación en curso para este servicio.");

            var activeSlot  = config.ActiveSlot;
            var standbySlot = activeSlot == InstanceSlot.Blue ? InstanceSlot.Green : InstanceSlot.Blue;

            Log(serviceId, "INFO",
                $"Iniciando rolling update: {activeSlot} → {standbySlot} con {Path.GetFileName(newJarPath)}");

            state.OverallStatus = ServiceStatus.Updating;
            NotifyStateChange(state);

            try
            {
                // ── Paso 1: Detener instancia standby (por si quedó de un update previo fallido)
                var oldStandby = GetSlotInstance(state, standbySlot);
                if (oldStandby is { Status: not ServiceStatus.Stopped })
                {
                    Log(serviceId, "INFO", $"Limpiando instancia standby anterior ({standbySlot})…");
                    await _launcher.StopAsync(oldStandby);
                    AssignSlot(state, standbySlot, null);
                }

                // ── Paso 2: Arrancar nuevo JAR en el slot standby
                var tempConfig = CloneConfigWithJar(config, newJarPath);
                Log(serviceId, "INFO", $"Arrancando {standbySlot} en nuevo JAR…");
                var newInstance = _launcher.Start(tempConfig, standbySlot);
                AssignSlot(state, standbySlot, newInstance);

                // ── Paso 3: Esperar health-check
                Log(serviceId, "INFO", $"Esperando health-check ({config.HealthCheckTimeoutSeconds}s)…");
                bool healthy = await _healthCheck.WaitForReadyAsync(
                    newInstance.InternalPort,
                    config.HealthCheckPath,
                    config.HealthCheckTimeoutSeconds,
                    config.HealthCheckIntervalSeconds,
                    newInstance.Process,
                    ct);

                if (!healthy)
                    throw new TimeoutException(
                        $"Health-check falló para {standbySlot} en {config.HealthCheckTimeoutSeconds}s. " +
                        "Instancia activa sin cambios.");

                newInstance.Status = ServiceStatus.Running;
                Log(serviceId, "INFO", $"{standbySlot} saludable en puerto {newInstance.InternalPort}");

                // ── Paso 4: Cambiar proxy → nuevo backend (sin downtime)
                _proxy.UpdateTarget(serviceId, newInstance.InternalPort);
                config.ActiveSlot = standbySlot;
                Log(serviceId, "INFO",
                    $"Proxy redirigido → {standbySlot}:{newInstance.InternalPort}. Tráfico migrado.");

                // ── Paso 5: Drain delay (dejar que las conexiones activas terminen)
                if (config.DrainDelayMilliseconds > 0)
                {
                    Log(serviceId, "INFO",
                        $"Drain delay {config.DrainDelayMilliseconds}ms…");
                    await Task.Delay(config.DrainDelayMilliseconds, ct);
                }

                // ── Paso 6: Apagar instancia anterior
                var oldActive = GetSlotInstance(state, activeSlot);
                if (oldActive is not null)
                {
                    Log(serviceId, "INFO", $"Apagando instancia anterior ({activeSlot})…");
                    await _launcher.StopAsync(oldActive);
                    AssignSlot(state, activeSlot, null);
                }

                // ── Actualizar configuración persistida
                config.JarPath       = newJarPath;
                config.LastUpdatedAt = DateTime.UtcNow;
                config.DeploymentHistory.Insert(0, new DeploymentRecord
                {
                    JarPath        = newJarPath,
                    JarFileName    = Path.GetFileName(newJarPath),
                    DeployedToSlot = standbySlot,
                    Success        = true,
                    Notes          = "Rolling update exitoso"
                });

                state.OverallStatus = ServiceStatus.Running;
                Log(serviceId, "INFO",
                    $"✅ Rolling update completado. Activo: {standbySlot}:{newInstance.InternalPort}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // ── Rollback: detener la instancia standby nueva si existe
                var failedStandby = GetSlotInstance(state, standbySlot);
                if (failedStandby is { Status: not ServiceStatus.Stopped })
                {
                    await _launcher.StopAsync(failedStandby);
                    AssignSlot(state, standbySlot, null);
                }

                state.OverallStatus = ServiceStatus.Error;
                state.LastError     = ex.Message;
                Log(serviceId, "ERROR", $"❌ Rolling update falló: {ex.Message}. Instancia activa sin cambios.");

                config.DeploymentHistory.Insert(0, new DeploymentRecord
                {
                    JarPath        = newJarPath,
                    JarFileName    = Path.GetFileName(newJarPath),
                    DeployedToSlot = standbySlot,
                    Success        = false,
                    Notes          = ex.Message
                });

                // Restaurar estado Running si la instancia activa sigue viva
                var activeInst = GetSlotInstance(state, activeSlot);
                if (activeInst?.Status == ServiceStatus.Running)
                    state.OverallStatus = ServiceStatus.Running;
            }
            finally { svcLock.Release(); }

            NotifyStateChange(state);
            await _persistence.UpsertServiceAsync(config, ct);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Watchdog — detecta muertes externas de procesos cada 5 s
        // ═══════════════════════════════════════════════════════════════════════
        private async Task WatchdogLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(5_000, ct); } catch (OperationCanceledException) { break; }

                foreach (var state in _states.Values)
                {
                    CheckInstance(state, state.Blue);
                    CheckInstance(state, state.Green);
                }
            }
        }

        private void CheckInstance(ServiceRuntimeState state, ServiceInstance? inst)
        {
            if (inst is null) return;
            // Solo vigilar instancias que deberían estar vivas
            if (inst.Status is ServiceStatus.Stopped or ServiceStatus.Error) return;
            if (inst.Process is null) return;

            bool exited;
            try   { exited = inst.Process.HasExited; }
            catch { return; } // handle ya liberado, ignorar

            if (!exited) return;

            var code = -1;
            try { code = inst.Process.ExitCode; } catch { }

            ProcessLauncher.Diag($"[Watchdog] PID={inst.ProcessId} ({inst.Slot}) murió externamente exitCode={code}");
            Log(state.Config.Id, "WARN",
                $"⚠️ Instancia {inst.Slot} terminó inesperadamente (exit code: {code}). " +
                "Usa 'Reiniciar' para recuperar el servicio.");

            inst.Status = ServiceStatus.Error;

            // Solo marcar el servicio como Error si esta instancia era la activa
            var isActive = (inst.Slot == state.Config.ActiveSlot);
            if (isActive)
            {
                state.OverallStatus = ServiceStatus.Error;
                state.LastError = $"Proceso terminó inesperadamente (exit code: {code})";
            }
            NotifyStateChange(state);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Queries
        // ═══════════════════════════════════════════════════════════════════════
        public IEnumerable<ServiceRuntimeState> GetAllStates() => _states.Values;
        public ServiceRuntimeState? GetState(string serviceId) =>
            _states.TryGetValue(serviceId, out var s) ? s : null;

        // ═══════════════════════════════════════════════════════════════════════
        //  Privados
        // ═══════════════════════════════════════════════════════════════════════
        private void OnChildLog(LogEntry entry)
        {
            PushToBuffer(entry);
            LogReceived?.Invoke(entry);
        }

        private void Log(string serviceId, string level, string msg)
        {
            var entry = new LogEntry
            {
                ServiceId = serviceId,
                Level     = level,
                Message   = msg
            };
            PushToBuffer(entry);
            LogReceived?.Invoke(entry);
        }

        private void PushToBuffer(LogEntry entry)
        {
            var queue = _logBuffers.GetOrAdd(entry.ServiceId, _ => new ConcurrentQueue<LogEntry>());
            queue.Enqueue(entry);
            // Mantener tamaño máximo
            while (queue.Count > LogBufferMaxSize)
                queue.TryDequeue(out _);
        }

        /// <summary>
        /// Devuelve todos los logs buffereados para un servicio.
        /// La UI lo llama al crear/seleccionar un ServiceItemViewModel.
        /// </summary>
        public IEnumerable<LogEntry> GetBufferedLogs(string serviceId) =>
            _logBuffers.TryGetValue(serviceId, out var q) ? q.ToArray() : Array.Empty<LogEntry>();

        private void NotifyStateChange(ServiceRuntimeState state) =>
            StateChanged?.Invoke(state);

        private static void AssignSlot(ServiceRuntimeState state, InstanceSlot slot, ServiceInstance? inst)
        {
            if (slot == InstanceSlot.Blue)  state.Blue  = inst;
            else                            state.Green = inst;
        }

        private static ServiceInstance? GetSlotInstance(ServiceRuntimeState state, InstanceSlot slot) =>
            slot == InstanceSlot.Blue ? state.Blue : state.Green;

        /// <summary>Clona la config sobreescribiendo solo el JarPath.</summary>
        private static ServiceConfig CloneConfigWithJar(ServiceConfig src, string newJar)
        {
            var clone = new ServiceConfig
            {
                Id                       = src.Id,
                Name                     = src.Name,
                JarPath                  = newJar,
                ConfigFilePath           = src.ConfigFilePath,
                PublicPort               = src.PublicPort,
                JvmArguments             = src.JvmArguments,
                JavaExecutable           = src.JavaExecutable,
                HealthCheckTimeoutSeconds = src.HealthCheckTimeoutSeconds,
                HealthCheckIntervalSeconds = src.HealthCheckIntervalSeconds,
                HealthCheckPath          = src.HealthCheckPath,
                AutoStart                = src.AutoStart,
                DrainDelayMilliseconds   = src.DrainDelayMilliseconds,
                ActiveSlot               = src.ActiveSlot,
            };
            return clone;
        }

        public async ValueTask DisposeAsync()
        {
            // Detener watchdog
            _watchdogCts.Cancel();
            if (_watchdogTask is not null)
                try { await _watchdogTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

            foreach (var state in _states.Values)
                await StopAsync(state.Config.Id);
            await _proxy.DisposeAsync();
        }
    }
}
