using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollingUpdateManager.Models;
using RollingUpdateManager.Services;

namespace RollingUpdateManager.ViewModels
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  ViewModel de un servicio individual en la lista
    // ═══════════════════════════════════════════════════════════════════════════
    public partial class ServiceItemViewModel : ObservableObject
    {
        private readonly ServiceOrchestrator _orchestrator;
        private CancellationTokenSource?     _cts;

        // Cancela la operación en curso (Start/Stop/Update/Redeploy)
        public void CancelCurrentOperation()
        {
            var cts = Interlocked.Exchange(ref _cts, null);
            cts?.Cancel();
            cts?.Dispose();
        }

        private CancellationToken NewCts()
        {
            var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            old?.Cancel();
            old?.Dispose();
            return _cts!.Token;
        }

        // ── Propiedades enlazadas ──────────────────────────────────────────────
        [ObservableProperty] private string         _id            = string.Empty;
        [ObservableProperty] private string         _name          = string.Empty;
        [ObservableProperty] private ServiceStatus  _status        = ServiceStatus.Stopped;
        [ObservableProperty] private int            _publicPort;
        [ObservableProperty] private int            _activeInternalPort;
        [ObservableProperty] private string         _activeSlotLabel = "—";
        [ObservableProperty] private string         _uptime          = "—";
        [ObservableProperty] private string         _lastError       = string.Empty;
        [ObservableProperty] private bool           _isBlueActive;
        [ObservableProperty] private bool           _isGreenActive;
        [ObservableProperty] private bool           _isBusy;

        // ── Métricas del proxy (actualizadas por el timer global de MainViewModel) ─
        [ObservableProperty] private string _reqPerSec    = "0.0";
        [ObservableProperty] private string _avgLatencyMs = "—";
        [ObservableProperty] private string _errorRate    = "0.0%";

        // Tiempo de ejecución continua sin redeploy ni restart manual.
        // Se muestra como "12d 03h 45m" o "—" si nunca llegó a Running.
        [ObservableProperty] private string _stableUptimeLabel = "—";

        // ── Slot seleccionado para los logs ─────────────────────────────────
        // null = mostrar todos los logs mezclados (comportamiento anterior)
        [ObservableProperty] private InstanceSlot? _viewingSlot = null;

        // Indica si cada slot tiene una instancia actualmente viva (para habilitar la tab)
        [ObservableProperty] private bool _hasBlueInstance;
        [ObservableProperty] private bool _hasGreenInstance;

        // Slot que recibe tráfico del proxy en este momento
        [ObservableProperty] private InstanceSlot? _liveSlot;

        // Re-evaluar CanExecute de todos los comandos cuando cambia Status o IsBusy
        partial void OnStatusChanged(ServiceStatus value)
        {
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            RestartCommand.NotifyCanExecuteChanged();
            RedeployCommand.NotifyCanExecuteChanged();
            UpdateJarCommand.NotifyCanExecuteChanged();
        }
        partial void OnIsBusyChanged(bool value)
        {
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            RestartCommand.NotifyCanExecuteChanged();
            RedeployCommand.NotifyCanExecuteChanged();
            UpdateJarCommand.NotifyCanExecuteChanged();
        }

        // Texto de logs por slot — cada uno tiene su propio StringBuilder
        // El code-behind NO recibe un BeginInvoke por línea: usa FlushPendingLog()
        // llamado por el _logFlushTimer de MainViewModel cada 50 ms.

        private readonly System.Text.StringBuilder _logSbBlue     = new();
        private readonly System.Text.StringBuilder _logSbGreen    = new();
        private readonly System.Text.StringBuilder _logSbCombined = new();
        private int _logLineCountBlue;
        private int _logLineCountGreen;
        private int _logLineCountCombined;
        private const int MaxLogLines     = 2000;  // máximo en los buffers internos
        private const int MaxDisplayLines = 400;   // máximo que se renderiza en el TextBox
        private readonly object _logLock = new();

        // Buffer de líneas pendientes de flush al TextBox (solo el slot visible).
        // AddLog escribe aquí sin tocar el Dispatcher; FlushPendingLog lo drena en el UI thread.
        private readonly System.Text.StringBuilder _pendingFlush = new();
        private readonly object _flushLock = new();

        // Generation counter: se incrementa cada vez que cambia el slot visible.
        private int _viewGen;
        public  int ViewGen => _viewGen;

        // Notifica al code-behind que cambió el slot de visualización (recarga completa)
        public event Action? SlotViewChanged;

        partial void OnViewingSlotChanged(InstanceSlot? value)
        {
            // Incrementar generación y limpiar buffer de flush pendiente:
            // las líneas acumuladas del slot anterior no deben aparecer en el nuevo.
            lock (_flushLock)
            {
                Interlocked.Increment(ref _viewGen);
                _pendingFlush.Clear();
            }
            SlotViewChanged?.Invoke();
        }

        /// <summary>
        /// Llamado por el _logFlushTimer de MainViewModel (50 ms) en el UI thread.
        /// Devuelve el texto acumulado desde el último flush y limpia el buffer.
        /// Retorna null si no hay nada pendiente (evita AppendText vacío).
        /// </summary>
        public string? TakePendingFlush()
        {
            lock (_flushLock)
            {
                if (_pendingFlush.Length == 0) return null;
                var text = _pendingFlush.ToString();
                _pendingFlush.Clear();
                return text;
            }
        }

        /// <summary>Devuelve el buffer del slot actualmente visible (para carga inicial).</summary>
        public string GetCurrentLogText() => GetCurrentLogTextTruncated(MaxDisplayLines);

        /// <summary>
        /// Devuelve las últimas <paramref name="maxLines"/> líneas del buffer visible.
        /// Evita asignar strings enormes al TextBox de WPF, que mide cada carácter en el UI thread.
        /// </summary>
        public string GetCurrentLogTextTruncated(int maxLines)
        {
            lock (_logLock)
            {
                var sb = ViewingSlot switch
                {
                    InstanceSlot.Blue  => _logSbBlue,
                    InstanceSlot.Green => _logSbGreen,
                    _                  => _logSbCombined
                };
                return TailOfStringBuilder(sb, maxLines);
            }
        }

        private static string TailOfStringBuilder(System.Text.StringBuilder sb, int maxLines)
        {
            var full  = sb.ToString();
            if (full.Length == 0) return full;

            // Contar \n desde el final para encontrar el inicio de las últimas maxLines
            int newlines = 0;
            int pos      = full.Length - 1;
            // Ignorar newline final si existe
            if (full[pos] == '\n') { pos--; }
            while (pos >= 0)
            {
                if (full[pos] == '\n')
                {
                    newlines++;
                    if (newlines == maxLines) { pos++; break; }
                }
                pos--;
            }
            return pos <= 0 ? full : full.Substring(pos);
        }

        // ── Constructor ──────────────────────────────────────────────────
        public ServiceItemViewModel(ServiceRuntimeState state, ServiceOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
            Update(state);

            // Cargar logs buffereados en los buffers por slot
            foreach (var entry in orchestrator.GetBufferedLogs(state.Config.Id))
            {
                var line = entry.ToString();
                _logSbCombined.AppendLine(line);
                _logLineCountCombined++;
                if (entry.Slot == InstanceSlot.Blue)
                { _logSbBlue.AppendLine(line); _logLineCountBlue++; }
                else
                { _logSbGreen.AppendLine(line); _logLineCountGreen++; }
            }
            // _logText eliminado: el code-behind lee el buffer via GetCurrentLogText()
        }

        // Llamado por el timer global de MainViewModel (1 Hz), no por un timer propio.
        // Evita tener N DispatcherTimers disparándose independientemente.
        public void RefreshMetrics()
        {
            var state = _orchestrator.GetState(Id);
            if (state is null) return;
            var (rps, latMs, errPct) = state.Metrics.GetSnapshot();

            // Solo asignar si cambió: cada set dispara PropertyChanged + re-evaluación
            // del binding en WPF. Con 6 VMs × 1 Hz esto ahorra ~18 disparos/seg ociosos.
            var newRps  = $"{rps:F1}";
            var newLat  = latMs > 0 ? $"{latMs:F0}" : "—";
            var newErr  = $"{errPct:F1}%";
            if (ReqPerSec    != newRps)  ReqPerSec    = newRps;
            if (AvgLatencyMs != newLat)  AvgLatencyMs = newLat;
            if (ErrorRate    != newErr)  ErrorRate    = newErr;

            // Uptime estable: tiempo sin redeploy ni restart manual
            var newStable = state.StableUptimeSince.HasValue
                ? FormatStableUptime(DateTime.UtcNow - state.StableUptimeSince.Value)
                : "—";
            if (StableUptimeLabel != newStable) StableUptimeLabel = newStable;

            // Uptime de la instancia activa: se actualiza aquí (1 Hz) en lugar de en
            // Update() que solo se llama en cambios de estado. Esto evita llamar
            // StateChanged cada segundo solo para actualizar el contador de tiempo.
            var active = state.ActiveInstance;
            if (active is not null)
            {
                var up = FormatUptime(active.Uptime);
                if (Uptime != up) Uptime = up;
            }
        }

        private static string FormatStableUptime(TimeSpan t)
        {
            // Granularidad reducida: no incluir segundos cuando hay días u horas.
            // Así PropertyChanged solo se dispara 1 vez por minuto (o por hora/día),
            // en lugar de cada segundo — ahorra ~5 PropertyChanged/seg con 6 servicios.
            if (t.TotalDays >= 1)    return $"{(int)t.TotalDays}d {t.Hours:D2}h {t.Minutes:D2}m";
            if (t.TotalHours >= 1)   return $"{t.Hours:D2}h {t.Minutes:D2}m";
            if (t.TotalMinutes >= 1) return $"{t.Minutes:D2}m {t.Seconds:D2}s";
            return $"{(int)t.TotalSeconds}s";
        }

        // ── Sincronizar desde estado runtime ──────────────────────────────────
        public void Update(ServiceRuntimeState state)
        {
            // Solo asignar si cambió: evita disparar PropertyChanged + re-render de binding
            // en todos los controles enlazados cuando el estado no ha variado.
            if (Id         != state.Config.Id)       Id         = state.Config.Id;
            if (Name       != state.Config.Name)     Name       = state.Config.Name;
            if (Status     != state.OverallStatus)   Status     = state.OverallStatus;
            if (PublicPort != state.Config.PublicPort) PublicPort = state.Config.PublicPort;
            var err = state.LastError ?? string.Empty;
            if (LastError  != err) LastError = err;

            var active = state.ActiveInstance;
            if (active is not null)
            {
                if (ActiveInternalPort != active.InternalPort)  ActiveInternalPort = active.InternalPort;
                var slotLabel = active.Slot.ToString();
                if (ActiveSlotLabel != slotLabel)               ActiveSlotLabel    = slotLabel;
                var up = FormatUptime(active.Uptime);
                if (Uptime != up)                               Uptime             = up;
                var blueActive  = active.Slot == InstanceSlot.Blue;
                var greenActive = active.Slot == InstanceSlot.Green;
                if (IsBlueActive  != blueActive)  IsBlueActive  = blueActive;
                if (IsGreenActive != greenActive) IsGreenActive = greenActive;
            }
            else
            {
                if (ActiveInternalPort != 0)   ActiveInternalPort = 0;
                if (ActiveSlotLabel    != "—") ActiveSlotLabel    = "—";
                if (Uptime             != "—") Uptime             = "—";
                if (IsBlueActive)  IsBlueActive  = false;
                if (IsGreenActive) IsGreenActive = false;
            }

            var busy = Status is ServiceStatus.Starting or ServiceStatus.Updating;
            if (IsBusy != busy) IsBusy = busy;

            var hasBlue  = state.Blue  is { Status: not ServiceStatus.Stopped };
            var hasGreen = state.Green is { Status: not ServiceStatus.Stopped };
            if (HasBlueInstance  != hasBlue)  HasBlueInstance  = hasBlue;
            if (HasGreenInstance != hasGreen) HasGreenInstance = hasGreen;

            var liveSlot = state.ActiveInstance?.Slot;
            if (LiveSlot != liveSlot) LiveSlot = liveSlot;
        }

        // ── Comandos ───────────────────────────────────────────────────────────
        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            await _orchestrator.StartAsync(Id, NewCts());
        }
        private bool CanStart() => (Status is ServiceStatus.Stopped or ServiceStatus.Error) && !IsBusy;

        [RelayCommand(CanExecute = nameof(CanStop))]
        private async Task StopAsync()
        {
            await _orchestrator.StopAsync(Id, NewCts());
        }
        private bool CanStop() => (Status is ServiceStatus.Running or ServiceStatus.Updating) && !IsBusy;

        [RelayCommand(CanExecute = nameof(CanStop))]
        private async Task RestartAsync()
        {
            await _orchestrator.RestartAsync(Id, NewCts());
        }

        /// <summary>
        /// Redespliega el mismo JAR que ya está configurado, sin necesidad de seleccionar
        /// un nuevo archivo. Útil cuando el JAR original fue reemplazado en disco.
        /// Usa el rolling update (blue/green) para no interrumpir el tráfico.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanRedeploy))]
        private async Task RedeployAsync()
        {
            var state = _orchestrator.GetState(Id);
            if (state is null) return;
            var jarPath = state.Config.JarPath;

            if (!System.IO.File.Exists(jarPath))
            {
                System.Windows.MessageBox.Show(
                    $"No se encontró el JAR:\n{jarPath}",
                    "Redeploy", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            try
            {
                await _orchestrator.RollingUpdateAsync(Id, jarPath, NewCts());
            }
            catch (InvalidOperationException ex)
            {
                System.Windows.MessageBox.Show(
                    $"No se puede iniciar el redeploy:\n{ex.Message}",
                    "Redeploy en curso",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Error durante el redeploy:\n{ex.Message}",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        private bool CanRedeploy() => Status is ServiceStatus.Running && !IsBusy;

        [RelayCommand(CanExecute = nameof(CanUpdateJar))]
        private async Task UpdateJarAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Seleccionar nuevo JAR",
                Filter = "JAR files (*.jar)|*.jar"
            };
            if (dialog.ShowDialog() != true) return;

            var jarPath = dialog.FileName;
            try
            {
                await _orchestrator.RollingUpdateAsync(Id, jarPath, NewCts());
            }
            catch (InvalidOperationException ex)
            {
                // Ocurre cuando ya hay una operación en curso (svcLock.WaitAsync(0) devolvió false)
                System.Windows.MessageBox.Show(
                    $"No se puede iniciar el update:\n{ex.Message}",
                    "Update en curso",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Error durante el rolling update:\n{ex.Message}",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        private bool CanUpdateJar() => Status is ServiceStatus.Running && !IsBusy;

        /// <summary>
        /// Mata el proceso (si existe) que está ocupando el puerto público del servicio.
        /// Útil cuando un proceso fantasma bloquea el puerto impidiendo el arranque.
        /// </summary>
        [RelayCommand]
        private async Task KillPortAsync()
        {
            int port = PublicPort;
            if (port <= 0) return;

            // Encontrar el PID que escucha en ese puerto con netstat
            string? output = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netstat", $"-ano")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"No se pudo ejecutar netstat:\n{ex.Message}",
                    "Kill Port", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return;
            }

            // Buscar líneas que contengan ":puerto " en estado LISTENING o ESTABLISHED
            var pids = new System.Collections.Generic.HashSet<int>();
            foreach (var line in output.Split('\n'))
            {
                // Ejemplo: "  TCP    0.0.0.0:8080           0.0.0.0:0              LISTENING       1234"
                if (!line.Contains($":{port} ", StringComparison.Ordinal)) continue;
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && int.TryParse(parts[^1], out int pid) && pid > 0)
                    pids.Add(pid);
            }

            if (pids.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    $"No se encontró ningún proceso escuchando en el puerto {port}.",
                    "Kill Port", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                $"Se van a terminar {pids.Count} proceso(s) en el puerto {port}:\nPIDs: {string.Join(", ", pids)}\n\n¿Continuar?",
                "Kill Port", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            int killed = 0;
            var errors = new System.Text.StringBuilder();
            foreach (int pid in pids)
            {
                try
                {
                    using var victim = System.Diagnostics.Process.GetProcessById(pid);
                    victim.Kill(entireProcessTree: false);
                    killed++;
                }
                catch (Exception ex)
                {
                    errors.AppendLine($"PID {pid}: {ex.Message}");
                }
            }

            string msg = killed > 0
                ? $"✅ {killed} proceso(s) terminado(s) en el puerto {port}."
                : $"No se pudo terminar ningún proceso.";
            if (errors.Length > 0) msg += $"\n\nErrores:\n{errors}";

            System.Windows.MessageBox.Show(msg, "Kill Port",
                System.Windows.MessageBoxButton.OK,
                killed > 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Error);
        }

        public void AddLog(string line, InstanceSlot slot)
        {
            lock (_logLock)
            {
                // ── Combined buffer ──
                _logSbCombined.AppendLine(line);
                _logLineCountCombined++;
                if (_logLineCountCombined > MaxLogLines)
                    TrimBuffer(_logSbCombined, ref _logLineCountCombined);

                // ── Per-slot buffer ──
                if (slot == InstanceSlot.Blue)
                {
                    _logSbBlue.AppendLine(line);
                    _logLineCountBlue++;
                    if (_logLineCountBlue > MaxLogLines)
                        TrimBuffer(_logSbBlue, ref _logLineCountBlue);
                }
                else
                {
                    _logSbGreen.AppendLine(line);
                    _logLineCountGreen++;
                    if (_logLineCountGreen > MaxLogLines)
                        TrimBuffer(_logSbGreen, ref _logLineCountGreen);
                }
            }

            // Si la línea pertenece al slot actualmente visible, acumularla en
            // _pendingFlush para que el timer de 50 ms la envíe al TextBox.
            // NO hacemos BeginInvoke aquí: eso es lo que causaba el freeze.
            bool isVisible = ViewingSlot is null || ViewingSlot == slot;
            if (isVisible)
            {
                lock (_flushLock)
                    _pendingFlush.AppendLine(line);
            }
        }

        /// <summary>
        /// Llamado desde el UI thread (por el _logFlushTimer de MainViewModel).
        /// Devuelve el texto acumulado y lo envía al TextBox sin crear lambdas.
        /// </summary>
        public void FlushPendingLog()
        {
            var text = TakePendingFlush();
            if (text is null) return;
            // La llamada viene del UI thread — el caller (MainWindow) hace el AppendText+Scroll.
            PendingLogReady?.Invoke(text);
        }

        /// <summary>
        /// Raised en el UI thread con el texto batch listo para AppendText.
        /// El handler en MainWindow.xaml.cs hace AppendText + ScrollToBottom condicionalmente.
        /// </summary>
        public event Action<string>? PendingLogReady;


        private static void TrimBuffer(System.Text.StringBuilder sb, ref int count)
        {
            // Encontrar el punto de corte (mitad de las líneas) sin Split:
            // Split('\'n') aloca un array de 2000 strings en heap con cada trim.
            // En su lugar escaneamos el string una sola vez buscando el newline N/2.
            var full    = sb.ToString();
            int target  = count / 2;   // líneas a eliminar
            int pos     = 0;
            int found   = 0;
            while (pos < full.Length && found < target)
            {
                if (full[pos] == '\n') found++;
                pos++;
            }
            sb.Clear();
            if (pos < full.Length)
                sb.Append(full, pos, full.Length - pos);
            count -= found;
        }

        private static string FormatUptime(TimeSpan t)
        {
            // Granularidad reducida para servicios con largo tiempo de ejecución:
            // evita PropertyChanged (y re-render del item de lista) cada segundo
            // cuando el servicio lleva horas corriendo.
            if (t.TotalDays >= 1)   return $"{(int)t.TotalDays}d {t.Hours:D2}h {t.Minutes:D2}m";
            if (t.TotalHours >= 1)  return $"{t.Hours:D2}h {t.Minutes:D2}m";
            if (t.TotalMinutes >= 5) return $"{t.Minutes:D2}m";
            return $"{t.Minutes:D2}m {t.Seconds:D2}s";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ViewModel principal de la ventana
    // ═══════════════════════════════════════════════════════════════════════════
    public partial class MainViewModel : ObservableObject
    {
        private readonly ServiceOrchestrator _orchestrator;

        public ObservableCollection<ServiceItemViewModel> Services { get; } = new();

        // Lookup rápido thread-safe: los logs llegan desde threads de background
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServiceItemViewModel>
            _serviceMap = new();

        [ObservableProperty] private ServiceItemViewModel? _selectedService;
        [ObservableProperty] private string _statusBarText = "Listo";

        // Un único timer para actualizar métricas de TODOS los servicios.
        // Sustituye los N DispatcherTimers individuales (uno por ServiceItemViewModel).
        private readonly DispatcherTimer _metricsTimer;

        public MainViewModel(ServiceOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;

            _metricsTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _metricsTimer.Tick += (_, _) =>
            {
                foreach (var svc in Services)
                    svc.RefreshMetrics();
            };
            _metricsTimer.Start();

            // Flush de logs: agrupa las líneas acumuladas en el buffer del VM seleccionado
            // en un solo AppendText cada 50 ms, en lugar de un BeginInvoke por línea.
            _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _logFlushTimer.Tick += (_, _) => SelectedService?.FlushPendingLog();
            _logFlushTimer.Start();
        }

        private int _initialized = 0;

        // ── Inicializar ─────────────────────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            // Prevenir doble inicialización si el evento Loaded se dispara más de una vez
            if (Interlocked.Exchange(ref _initialized, 1) == 1) return;
            await _orchestrator.InitializeAsync();

            foreach (var state in _orchestrator.GetAllStates())
            {
                var vm = new ServiceItemViewModel(state, _orchestrator);
                Services.Add(vm);
                _serviceMap[state.Config.Id] = vm;
                Diag($"InitializeAsync: registrado serviceId={state.Config.Id.Substring(0,8)} name='{state.Config.Name}'");
            }

            Diag($"InitializeAsync: total servicios={Services.Count} mapSize={_serviceMap.Count}");

            // Suscribir eventos AHORA que el mapa está listo.
            // Los logs del arranque inicial ya están en el ring-buffer del orquestador
            // y se cargaron en el constructor de cada ServiceItemViewModel.
            _orchestrator.StateChanged += OnStateChanged;
            _orchestrator.LogReceived  += OnLogReceived;

            // Auto-seleccionar el primer servicio para que los logs sean visibles de inmediato
            if (Services.Count > 0)
                SelectedService = Services[0];

            StatusBarText = $"Iniciado — {Services.Count} servicio(s) registrado(s)";
        }

        // ── Comandos ───────────────────────────────────────────────────────────
        [RelayCommand]
        private void AddService()
        {
            var dialog = new Views.AddEditServiceDialog();
            if (dialog.ShowDialog() != true) return;
            AddServiceAsync(dialog.ResultConfig!)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Application.Current.Dispatcher.Invoke(() =>
                            MessageBox.Show(
                                $"Error al agregar servicio:\n{t.Exception!.InnerException?.Message ?? t.Exception.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error));
                }, TaskScheduler.Default);
        }

        private async Task AddServiceAsync(ServiceConfig config)
        {
            await _orchestrator.AddOrUpdateServiceAsync(config);
            var state = _orchestrator.GetState(config.Id)!;

            // VM en el mapa ANTES de llamar StartAsync para que los logs no se pierdan
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var vm = new ServiceItemViewModel(state, _orchestrator);
                Services.Add(vm);
                _serviceMap[config.Id] = vm;
                SelectedService = vm;
            });

            StatusBarText = $"Servicio '{config.Name}' registrado.";

            if (config.AutoStart)
                await _orchestrator.StartAsync(config.Id);
        }

        [RelayCommand(CanExecute = nameof(HasSelectedService))]
        private void EditService()
        {
            if (SelectedService is null) return;
            var state  = _orchestrator.GetState(SelectedService.Id)!;
            var dialog = new Views.AddEditServiceDialog(state.Config);
            if (dialog.ShowDialog() != true) return;
            _orchestrator.AddOrUpdateServiceAsync(dialog.ResultConfig!)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Application.Current.Dispatcher.Invoke(() =>
                            MessageBox.Show(
                                $"Error al guardar cambios:\n{t.Exception!.InnerException?.Message ?? t.Exception.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error));
                }, TaskScheduler.Default);
        }

        [RelayCommand(CanExecute = nameof(HasSelectedService))]
        private async Task RemoveServiceAsync()
        {
            if (SelectedService is null) return;
            var result = MessageBox.Show(
                $"¿Eliminar servicio '{SelectedService.Name}'?",
                "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            await _orchestrator.RemoveServiceAsync(SelectedService.Id);
            Application.Current.Dispatcher.Invoke(() =>
            {
                _serviceMap.TryRemove(SelectedService.Id, out _);
                Services.Remove(SelectedService);
            });

            StatusBarText = $"Servicio eliminado.";
        }

        [RelayCommand]
        private async Task StartAllAsync()
        {
            var tasks = new List<Task>();
            foreach (var svc in Services)
                if (svc.Status == ServiceStatus.Stopped || svc.Status == ServiceStatus.Error)
                    tasks.Add(_orchestrator.StartAsync(svc.Id));
            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        [RelayCommand]
        private async Task StopAllAsync()
        {
            var tasks = new List<Task>();
            foreach (var svc in Services)
                if (svc.Status == ServiceStatus.Running || svc.Status == ServiceStatus.Updating)
                    tasks.Add(_orchestrator.StopAsync(svc.Id));
            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Permite actualizar el propio exe del manager sin cerrarlo primero.
        /// El usuario selecciona el nuevo exe; la app programa el swap y se reinicia sola.
        /// Los JARs gestionados siguen corriendo durante el swap (son procesos independientes).
        /// </summary>
        [RelayCommand]
        private void UpdateManager()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Seleccionar nueva versión del manager",
                Filter = "Ejecutable (*.exe)|*.exe"
            };
            if (dialog.ShowDialog() != true) return;

            var result = System.Windows.MessageBox.Show(
                $"Se va a instalar:\n{dialog.FileName}\n\nLa aplicación se reiniciará automáticamente.\nLos servicios JAR continuarán ejecutándose.\n\n¿Continuar?",
                "Actualizar manager",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            // Ejecutar todo en background: File.Copy puede tardar (EXE de ~190MB en red o disco lento),
            // y el handoff interno tiene un Task.Run().Wait(5s).
            // Nada de esto debe bloquear el Dispatcher — la app se mantiene responsiva hasta Shutdown().
            StatusBarText = "Preparando actualización... no cierres la aplicación.";
            var newExePath = dialog.FileName;
            Task.Run(() => App.ScheduleUpdateAndRestart(newExePath));
        }

        private bool HasSelectedService() => SelectedService is not null;

        // Notificar los comandos que dependen de SelectedService cuando éste cambia
        partial void OnSelectedServiceChanged(ServiceItemViewModel? value)
        {
            EditServiceCommand.NotifyCanExecuteChanged();
            RemoveServiceCommand.NotifyCanExecuteChanged();
        }

        // ── Coalescing de StateChanged ─────────────────────────────────────
        // Por cada serviceId guardamos si ya hay un dispatch de Update pendiente.
        // Usamos int[] (array de 1 elemento) como wrapper de referencia para
        // poder hacer Interlocked.Exchange sobre el campo dentro del diccionario.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int[]>
            _pendingStateUpdate = new();

        // ── Handlers de eventos del orquestador ───────────────────────────────
        private void OnStateChanged(ServiceRuntimeState state)
        {
            if (!_serviceMap.TryGetValue(state.Config.Id, out var vm)) return;

            // Obtener (o crear) el flag box para este servicio.
            var box = _pendingStateUpdate.GetOrAdd(state.Config.Id, _ => new int[1]);

            // Si ya había un dispatch pendiente (flag=1), no creamos otro.
            // El BeginInvoke ya en vuelo leerá el estado más reciente cuando se ejecute.
            if (System.Threading.Interlocked.Exchange(ref box[0], 1) == 1)
                return;

            var serviceId = state.Config.Id;
            Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                () =>
                {
                    // Limpiar ANTES de leer: si otro StateChanged llega mientras
                    // ejecutamos vm.Update, generará su propio BeginInvoke.
                    System.Threading.Interlocked.Exchange(ref box[0], 0);
                    var latest = _orchestrator.GetState(serviceId);
                    if (latest is not null) vm.Update(latest);
                });
        }

        // ── Logs: acumulación + flush cada 50 ms ─────────────────────────────
        // Con 6 servicios logueando pueden llegar cientos de líneas/seg.
        // Un BeginInvoke por línea = cientos de layout-passes/seg = freeze.
        // Solución: AddLog escribe al buffer del VM (sin dispatch al UI);
        // _logFlushTimer flushea el TextBox visible en un único AppendText por tick.
        private readonly DispatcherTimer _logFlushTimer;

        private void OnLogReceived(LogEntry entry)
        {
            if (_serviceMap.TryGetValue(entry.ServiceId, out var vm))
                vm.AddLog(entry.ToString(), entry.Slot);
        }

        private static void Diag(string msg) =>
            RollingUpdateManager.Services.ProcessLauncher.Diag($"[VM] {msg}");
    }
}
