using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            // Precalentar el thread pool antes de lanzar los arranques en paralelo.
            // En un VPS con 2-4 vCPUs el pool empieza en ProcessorCount threads;
            // con 6 JARs arrancando a la vez el pool se satura y añade 1 thread/s.
            int autoStartCount = data.Services.Count(s => s.AutoStart);
            if (autoStartCount > 0)
            {
                int needed = Math.Max(Environment.ProcessorCount * 2, autoStartCount * 4);
                ThreadPool.GetMinThreads(out int curWorker, out int curIo);
                if (needed > curWorker)
                    ThreadPool.SetMinThreads(needed, Math.Max(curIo, needed));
            }

            foreach (var config in data.Services)
            {
                var state = new ServiceRuntimeState { Config = config };
                _states[config.Id] = state;
            }

            // Arranques escalonados: 2 segundos de offset entre cada JAR.
            // Evita que 6 JVMs compitan por CPU/disco durante su fase de inicio
            // y que todos los health-check timeouts expiren a la vez.
            int delay = 0;
            foreach (var config in data.Services)
            {
                if (!config.AutoStart) continue;
                var svcId      = config.Id;
                var startDelay = delay;
                _ = Task.Run(async () =>
                {
                    if (startDelay > 0)
                        await Task.Delay(startDelay, ct).ConfigureAwait(false);
                    await StartAsync(svcId, ct).ConfigureAwait(false);
                }, ct)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Log(svcId, "ERROR",
                            $"AutoStart falló: {t.Exception!.InnerException?.Message ?? t.Exception.Message}");
                }, TaskScheduler.Default);
                delay += 2000;  // 2 segundos entre cada JAR
            }

            // Iniciar watchdog que detecta procesos muertos externamente
            _watchdogTask = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token));
        }

        // ── Registrar / actualizar configuración ────────────────────────────────────────
        public async Task AddOrUpdateServiceAsync(ServiceConfig config, CancellationToken ct = default)
        {
            if (!_states.TryGetValue(config.Id, out var state))
            {
                // Servicio nuevo: crear estado y registrar
                _states[config.Id] = new ServiceRuntimeState { Config = config };
            }
            else
            {
                // Servicio existente: actualizar campos de configuración en el objeto existente
                // (no reemplazar la referencia para evitar races con operaciones en vuelo)
                var svcLock = _serviceLocks.GetOrAdd(config.Id, _ => new SemaphoreSlim(1, 1));
                await svcLock.WaitAsync(ct);

                int oldPublicPort = state.Config.PublicPort;
                bool portChanged  = config.PublicPort != oldPublicPort;

                try
                {
                    state.Config.Name                        = config.Name;
                    state.Config.JarPath                     = config.JarPath;
                    state.Config.ConfigFilePath              = config.ConfigFilePath;
                    state.Config.PublicPort                  = config.PublicPort;
                    state.Config.JvmArguments                = config.JvmArguments;
                    state.Config.JavaExecutable              = config.JavaExecutable;
                    state.Config.HealthCheckTimeoutSeconds   = config.HealthCheckTimeoutSeconds;
                    state.Config.HealthCheckIntervalSeconds  = config.HealthCheckIntervalSeconds;
                    state.Config.HealthCheckPath             = config.HealthCheckPath;
                    state.Config.AutoStart                   = config.AutoStart;
                    state.Config.DrainDelayMilliseconds      = config.DrainDelayMilliseconds;
                }
                finally { svcLock.Release(); }

                // Si el puerto público cambió y el proxy ya está activo, hacer rebind.
                // EL servicio sigue corriendo sin downtime: solo cambia el listener externo.
                if (portChanged && _proxy.GetCurrentPublicPort(config.Id) == oldPublicPort)
                {
                    var activeInst = state.ActiveInstance;
                    if (activeInst is not null)
                    {
                        Log(config.Id, "INFO",
                            $"Puerto público cambiado {oldPublicPort} → {config.PublicPort}. Rebinding proxy…");
                        try
                        {
                            await _proxy.RebindPublicPortAsync(
                                config.Id, config.PublicPort, activeInst.InternalPort, state.Metrics, ct);
                            Log(config.Id, "INFO",
                                $"Proxy relanzado correctamente en :{config.PublicPort}.");
                        }
                        catch (Exception ex)
                        {
                            Log(config.Id, "ERROR",
                                $"No se pudo relanzar el proxy en :{config.PublicPort} — {ex.Message}. " +
                                $"El tráfico sigue en :{oldPublicPort} hasta que reinicies el servicio.");
                            // Revertir puerto en config para no quedar en estado inconsistente
                            state.Config.PublicPort = oldPublicPort;
                            config.PublicPort       = oldPublicPort;
                        }
                    }
                    else
                    {
                        Log(config.Id, "INFO",
                            $"Puerto público actualizado a {config.PublicPort}. Se aplicará al próximo Start.");
                    }
                }
            }

            await _persistence.UpsertServiceAsync(config, ct);
        }

        // ── Eliminar servicio ────────────────────────────────────────────
        public async Task RemoveServiceAsync(string serviceId, CancellationToken ct = default)
        {
            if (_states.TryGetValue(serviceId, out var state))
                await StopInstancesAsync(state);
            await _proxy.RemoveProxyAsync(serviceId);
            _states.TryRemove(serviceId, out _);
            // Liberar el mutex del servicio para evitar leak en _serviceLocks
            if (_serviceLocks.TryRemove(serviceId, out var sem))
                sem.Dispose();
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

            // Reactivar AutoStart para que el próximo arranque de la app lo levante
            state.Config.AutoStart = true;

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

                // Marcar inicio del periodo estable (mismo JAR, mismo slot).
                // Solo se resetea en RollingUpdate o Restart manual, no en auto-restart del watchdog.
                state.StableUptimeSince = DateTime.UtcNow;

                // Levantar proxy si no existe o actualizar target
                await _proxy.EnsureProxyAsync(
                    serviceId, config.PublicPort, instance.InternalPort, state.Metrics, ct);

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
                    // Liberar el puerto explícitamente: si el proceso no llegó a Running
                    // es posible que process.Exited no se dispare (o se dispare tarde),
                    // dejando el puerto reservado sin un proceso que lo use.
                    if (failedInst.ReleasePort is { } rel)
                        rel();
                    else if (failedInst.InternalPort > 0)
                        _portManager.ReleasePort(failedInst.InternalPort);
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

            var svcLock = _serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
            // Esperar a que termine cualquier Start/Update en curso (timeout 30 s)
            if (!await svcLock.WaitAsync(TimeSpan.FromSeconds(30), ct))
            {
                Log(serviceId, "WARN", "Stop abortado: otra operación no cedió el lock en 30 s.");
                return;
            }

            try
            {
                if (state.OverallStatus == ServiceStatus.Stopped)
                {
                    Log(serviceId, "INFO", "El servicio ya estaba detenido.");
                    return;
                }

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
            }
            finally { svcLock.Release(); }

            NotifyStateChange(state);
            // Persistir: el próximo arranque de la app no autoiniciará este servicio
            // si el usuario lo detuvo explícitamente.
            state.Config.AutoStart = false;
            await _persistence.UpsertServiceAsync(state.Config, ct);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  RESTART
        // ═══════════════════════════════════════════════════════════════════════
        public async Task RestartAsync(string serviceId, CancellationToken ct = default)
        {
            // Restart manual: resetear uptime estable (el operador lo inició conscientemente)
            if (_states.TryGetValue(serviceId, out var stateForReset))
                stateForReset.StableUptimeSince = null;

            await StopAsync(serviceId, ct);
            await Task.Delay(1000, ct); // pequeño respiro
            // StopAsync marca AutoStart=false; restablecerlo antes del Start
            if (_states.TryGetValue(serviceId, out var state))
                state.Config.AutoStart = true;
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

            // Resetear el contador de uptime estable: el rolling update cambia el JAR/slot.
            state.StableUptimeSince = null;

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

                // Capturar referencia a la instancia vieja ANTES del drain
                // (necesaria para el rollback si el nuevo proceso muere durante el drain)
                var oldActive = GetSlotInstance(state, activeSlot);

                // ── Paso 5: Graceful drain — esperar a que las requests en vuelo al slot
                // anterior terminen. Las nuevas requests ya van al nuevo slot (UpdateTarget
                // se hizo arriba). Esto garantiza zero conexiones huérfanas.
                // Si el nuevo proceso muere durante el drain → auto-rollback inmediato.
                // Timeout de seguridad = DrainDelayMilliseconds (configurable por el usuario).
                {
                    int oldPort   = oldActive?.InternalPort ?? -1;
                    int timeoutMs = config.DrainDelayMilliseconds > 0
                        ? config.DrainDelayMilliseconds
                        : 10_000;   // fallback 10 s si el usuario dejó 0

                    Log(serviceId, "INFO",
                        $"Graceful drain: esperando requests en vuelo hacia {activeSlot}:{oldPort} " +
                        $"(timeout {timeoutMs} ms)…");

                    const int pollMs  = 100;
                    var deadline      = Environment.TickCount64 + timeoutMs;
                    int lastInFlight  = -1;

                    while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
                    {
                        int inFlight = oldPort > 0 ? _proxy.GetInFlight(serviceId, oldPort) : 0;

                        if (inFlight != lastInFlight)
                        {
                            lastInFlight = inFlight;
                            if (inFlight > 0)
                                Log(serviceId, "INFO",
                                    $"  → {inFlight} request(s) en vuelo hacia {activeSlot}, esperando…");
                        }

                        if (inFlight <= 0) break;

                        // CancellationToken.None: no interrumpir el drain por cancelación externa;
                        // las requests ya en vuelo deben terminar antes de matar el proceso viejo.
                        await Task.Delay(pollMs, CancellationToken.None).ConfigureAwait(false);

                        // Vigilar el nuevo proceso durante el drain
                        bool newExited;
                        try   { newExited = newInstance.Process?.HasExited ?? true; }
                        catch { newExited = true; }

                        if (newExited)
                        {
                            var code2 = -1;
                            try { code2 = newInstance.Process?.ExitCode ?? -1; } catch { }
                            throw new InvalidOperationException(
                                $"⚠️ La nueva instancia ({standbySlot}) terminó inesperadamente " +
                                $"durante el drain (exit code: {code2}). Realizando auto-rollback al slot {activeSlot}.");
                        }
                    }

                    int remaining = _proxy.GetInFlight(serviceId, oldPort);
                    if (remaining > 0)
                        Log(serviceId, "WARN",
                            $"Drain timeout alcanzado con {remaining} request(s) aún en vuelo en {activeSlot}. " +
                            "Procediendo a apagar la instancia anterior.");
                    else
                        Log(serviceId, "INFO",
                            $"Drain completado. Todas las requests de {activeSlot} han terminado.");
                }

                // Verificación final antes de matar la instancia vieja
                {
                    bool newExited;
                    try   { newExited = newInstance.Process?.HasExited ?? true; }
                    catch { newExited = true; }
                    if (newExited)
                    {
                        var code2 = -1;
                        try { code2 = newInstance.Process?.ExitCode ?? -1; } catch { }
                        throw new InvalidOperationException(
                            $"⚠️ La nueva instancia ({standbySlot}) no sobrevivió el drain " +
                            $"(exit code: {code2}). Realizando auto-rollback al slot {activeSlot}.");
                    }
                }

                // ── Paso 6: Apagar instancia anterior ────────────────────────────────────
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
                // Mantener histórico acotado para no crecer indefinidamente
                if (config.DeploymentHistory.Count > 20)
                    config.DeploymentHistory.RemoveRange(20, config.DeploymentHistory.Count - 20);

                state.OverallStatus = ServiceStatus.Running;
                Log(serviceId, "INFO",
                    $"✅ Rolling update completado. Activo: {standbySlot}:{newInstance.InternalPort}");

                // Nuevo periodo estable: JAR nuevo corriendo en slot nuevo.
                state.StableUptimeSince = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // ── Determinar si el proxy ya fue cambiado al nuevo slot ────────────────
                // Si config.ActiveSlot ya fue actualizado a standbySlot, significa que el
                // proxy fue conmutado pero el nuevo proceso murió → auto-rollback al slot viejo.
                bool proxyAlreadySwitched = (config.ActiveSlot == standbySlot);

                var failedStandby = GetSlotInstance(state, standbySlot);
                var oldActiveInst = GetSlotInstance(state, activeSlot);

                if (proxyAlreadySwitched && oldActiveInst is { Status: ServiceStatus.Running })
                {
                    // ── AUTO-ROLLBACK: reapuntar proxy al slot anterior (que sigue vivo) ──
                    _proxy.UpdateTarget(serviceId, oldActiveInst.InternalPort);
                    config.ActiveSlot = activeSlot;
                    Log(serviceId, "WARN",
                        $"🔄 Auto-rollback: proxy restaurado → {activeSlot}:{oldActiveInst.InternalPort}. " +
                        "Instancia anterior sigue recibiendo tráfico.");
                }
                else if (proxyAlreadySwitched && oldActiveInst is null)
                {
                    // La instancia antigua ya fue eliminada — el servicio queda en error
                    Log(serviceId, "ERROR",
                        "❌ Auto-rollback imposible: la instancia anterior ya fue eliminada antes del crash.");
                }

                // Detener la instancia standby fallida si sigue en pie
                if (failedStandby is { Status: not ServiceStatus.Stopped })
                {
                    await _launcher.StopAsync(failedStandby);
                    AssignSlot(state, standbySlot, null);
                }

                state.LastError = ex.Message;
                Log(serviceId, "ERROR", $"❌ Rolling update falló: {ex.Message}");

                config.DeploymentHistory.Insert(0, new DeploymentRecord
                {
                    JarPath        = newJarPath,
                    JarFileName    = Path.GetFileName(newJarPath),
                    DeployedToSlot = standbySlot,
                    Success        = false,
                    Notes          = ex.Message
                });
                if (config.DeploymentHistory.Count > 20)
                    config.DeploymentHistory.RemoveRange(20, config.DeploymentHistory.Count - 20);

                // Restaurar estado Running si la instancia activa sigue viva
                var activeInst = GetSlotInstance(state, activeSlot);
                if (proxyAlreadySwitched && GetSlotInstance(state, config.ActiveSlot) is { Status: ServiceStatus.Running })
                    state.OverallStatus = ServiceStatus.Running;
                else if (!proxyAlreadySwitched && activeInst?.Status == ServiceStatus.Running)
                    state.OverallStatus = ServiceStatus.Running;
                else
                    state.OverallStatus = ServiceStatus.Error;
            }
            finally { svcLock.Release(); }

            NotifyStateChange(state);
            await _persistence.UpsertServiceAsync(config, ct);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Watchdog — detecta muertes externas de procesos cada 5 s
        // ═══════════════════════════════════════════════════════════════════════

        // Por servicio: ventana de reintento (ticks) y contador de intentos recientes
        private readonly ConcurrentDictionary<string, (long WindowStart, int Attempts)> _restartAttempts = new();
        private const int MaxAutoRestarts   = 3;       // máximo reintentos en la ventana
        private const int RestartWindowMs   = 5 * 60 * 1000; // 5 minutos

        private async Task WatchdogLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(5_000, ct); } catch (OperationCanceledException) { break; }

                // Lanzar checks en paralelo: un auto-restart con backoff de 15s
                // no debe bloquear los checks de los otros 5 servicios.
                var checks = new List<Task>();
                foreach (var state in _states.Values)
                {
                    checks.Add(CheckInstanceAsync(state, state.Blue,  ct));
                    checks.Add(CheckInstanceAsync(state, state.Green, ct));
                }
                await Task.WhenAll(checks);
            }
        }

        private async Task CheckInstanceAsync(ServiceRuntimeState state, ServiceInstance? inst, CancellationToken ct)
        {
            if (inst is null) return;
            if (inst.Status is ServiceStatus.Stopped or ServiceStatus.Error) return;
            if (inst.Process is null) return;

            bool exited;
            try   { exited = inst.Process.HasExited; }
            catch { return; }
            if (!exited) return;

            var code = -1;
            try { code = inst.Process.ExitCode; } catch { }

            var svcId    = state.Config.Id;
            var isActive = inst.Slot == state.Config.ActiveSlot;

            ProcessLauncher.Diag($"[Watchdog] PID={inst.ProcessId} ({inst.Slot}) murió externamente exitCode={code}");
            Log(svcId, "WARN",
                $"⚠️ Instancia {inst.Slot} terminó inesperadamente (exit code: {code}).");

            inst.Status = ServiceStatus.Error;
            AssignSlot(state, inst.Slot, null);

            // Solo auto-reiniciar si era el slot activo
            if (!isActive)
            {
                NotifyStateChange(state);
                return;
            }

            state.OverallStatus = ServiceStatus.Error;
            state.LastError     = $"Proceso terminó inesperadamente (exit code: {code})";
            NotifyStateChange(state);

            // ── Auto-restart con backoff ──────────────────────────────────────────
            // Contar intentos en la ventana de 5 min; si se supera el máximo, rendir.
            var now = Environment.TickCount64;
            var (windowStart, attempts) = _restartAttempts.GetOrAdd(svcId, (now, 0));

            // Resetear ventana si han pasado más de 5 min desde el primer intento
            if (now - windowStart > RestartWindowMs)
            {
                windowStart = now;
                attempts    = 0;
            }

            if (attempts >= MaxAutoRestarts)
            {
                Log(svcId, "ERROR",
                    $"❌ Auto-restart deshabilitado para '{state.Config.Name}': {MaxAutoRestarts} intentos " +
                    $"en los últimos {RestartWindowMs / 60000} min. Intervención manual requerida.");
                _restartAttempts[svcId] = (windowStart, attempts);
                return;
            }

            attempts++;
            _restartAttempts[svcId] = (windowStart, attempts);

            // Backoff exponencial: 5s, 15s, 45s
            int backoffMs = (int)(5_000 * Math.Pow(3, attempts - 1));
            Log(svcId, "INFO",
                $"🔄 Auto-restart {attempts}/{MaxAutoRestarts} en {backoffMs / 1000}s " +
                $"para '{state.Config.Name}'…");

            try
            {
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                // StartAsync adquiere su propio lock; si ya hay otra operación en curso la ignora.
                await StartAsync(svcId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log(svcId, "ERROR", $"Auto-restart {attempts} fallido: {ex.Message}");
            }
        }

        // Mantener compatibilidad con referencias antiguas (CheckInstance era privado, no hay externas)
        private void CheckInstance(ServiceRuntimeState state, ServiceInstance? inst)
        {
            // redirigir a la versión async de forma fire-and-forget
            _ = CheckInstanceAsync(state, inst, _watchdogCts.Token);
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

            // Apagado silencioso: no tocar AutoStart ni persistir durante el cierre de la app
            var stops = new List<Task>();
            foreach (var state in _states.Values)
                stops.Add(StopInstancesAsync(state));
            await Task.WhenAll(stops);

            await _proxy.DisposeAsync();

            // Liberar todos los mutexes
            foreach (var sem in _serviceLocks.Values)
                sem.Dispose();
            _serviceLocks.Clear();
        }

        /// <summary>Para todas las instancias de un estado sin modificar la config ni persistir.</summary>
        private async Task StopInstancesAsync(ServiceRuntimeState state)
        {
            var tasks = new List<Task>();
            if (state.Blue  is { } b && b.Status != ServiceStatus.Stopped)
                tasks.Add(_launcher.StopAsync(b));
            if (state.Green is { } g && g.Status != ServiceStatus.Stopped)
                tasks.Add(_launcher.StopAsync(g));
            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
            state.Blue  = null;
            state.Green = null;
            state.OverallStatus = ServiceStatus.Stopped;
        }
    }
}
