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

        // Texto de logs acumulado — se enlaza directamente a un TextBox.Text
        // Es más confiable que ObservableCollection + ListBox para logs en tiempo real
        [ObservableProperty] private string _logText = string.Empty;
        private int _logLineCount = 0;
        private const int MaxLogLines = 2000;
        private readonly System.Text.StringBuilder _logSb = new();
        private readonly object _logLock = new();

        // Evento para que el code-behind haga AppendText directamente
        // (más eficiente que reemplazar todo el texto en cada log)
        public event Action<string>? NewLogLine;

        // ── Constructor ──────────────────────────────────────────────────
        public ServiceItemViewModel(ServiceRuntimeState state, ServiceOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
            Update(state);

            // Cargar logs buffereados
            foreach (var entry in orchestrator.GetBufferedLogs(state.Config.Id))
            {
                _logSb.AppendLine(entry.ToString());
                _logLineCount++;
            }
            _logText = _logSb.ToString();
        }

        // ── Sincronizar desde estado runtime ──────────────────────────────────
        public void Update(ServiceRuntimeState state)
        {
            Id        = state.Config.Id;
            Name      = state.Config.Name;
            Status    = state.OverallStatus;
            PublicPort = state.Config.PublicPort;
            LastError = state.LastError;

            var active = state.ActiveInstance;
            if (active is not null)
            {
                ActiveInternalPort = active.InternalPort;
                ActiveSlotLabel    = active.Slot.ToString();
                Uptime             = FormatUptime(active.Uptime);
                IsBlueActive       = active.Slot == InstanceSlot.Blue;
                IsGreenActive      = active.Slot == InstanceSlot.Green;
            }
            else
            {
                ActiveInternalPort = 0;
                ActiveSlotLabel    = "—";
                Uptime             = "—";
                IsBlueActive       = IsGreenActive = false;
            }

            IsBusy = Status is ServiceStatus.Starting or ServiceStatus.Updating;
        }

        // ── Comandos ───────────────────────────────────────────────────────────
        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            await _orchestrator.StartAsync(Id, _cts.Token);
        }
        private bool CanStart() => Status is ServiceStatus.Stopped or ServiceStatus.Error;

        [RelayCommand(CanExecute = nameof(CanStop))]
        private async Task StopAsync()
        {
            _cts = new CancellationTokenSource();
            await _orchestrator.StopAsync(Id, _cts.Token);
        }
        private bool CanStop() => Status is ServiceStatus.Running or ServiceStatus.Updating;

        [RelayCommand(CanExecute = nameof(CanStop))]
        private async Task RestartAsync()
        {
            _cts = new CancellationTokenSource();
            await _orchestrator.RestartAsync(Id, _cts.Token);
        }

        [RelayCommand]
        private async Task UpdateJarAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Seleccionar nuevo JAR",
                Filter = "JAR files (*.jar)|*.jar"
            };
            if (dialog.ShowDialog() != true) return;

            _cts = new CancellationTokenSource();
            await _orchestrator.RollingUpdateAsync(Id, dialog.FileName, _cts.Token);
        }

        public void AddLog(string line)
        {
            string? fullReset = null;
            string append = line + Environment.NewLine;

            lock (_logLock)
            {
                _logSb.AppendLine(line);
                _logLineCount++;
                if (_logLineCount > MaxLogLines)
                {
                    var lines = _logSb.ToString().Split('\n');
                    _logSb.Clear();
                    int start = lines.Length / 2;
                    for (int i = start; i < lines.Length; i++)
                    {
                        _logSb.Append(lines[i]);
                        if (i < lines.Length - 1) _logSb.Append('\n');
                    }
                    _logLineCount = lines.Length - start;
                    fullReset = _logSb.ToString();
                }
            }

            if (fullReset is not null)
                Application.Current.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => LogText = fullReset);
            else
                NewLogLine?.Invoke(append);
        }

        private static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1)   return $"{(int)t.TotalDays}d {t.Hours:D2}h {t.Minutes:D2}m";
            if (t.TotalHours >= 1)  return $"{t.Hours:D2}h {t.Minutes:D2}m {t.Seconds:D2}s";
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

        public MainViewModel(ServiceOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
            // Los eventos se suscriben en InitializeAsync, DESPUÉS de poblar _serviceMap
        }

        // ── Inicializar ────────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
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
            _ = AddServiceAsync(dialog.ResultConfig!);
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
            _ = _orchestrator.AddOrUpdateServiceAsync(dialog.ResultConfig!);
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
            foreach (var svc in Services)
                if (svc.Status == ServiceStatus.Stopped)
                    await _orchestrator.StartAsync(svc.Id);
        }

        [RelayCommand]
        private async Task StopAllAsync()
        {
            foreach (var svc in Services)
                if (svc.Status == ServiceStatus.Running)
                    await _orchestrator.StopAsync(svc.Id);
        }

        private bool HasSelectedService() => SelectedService is not null;

        // ── Handlers de eventos del orquestador ───────────────────────────────
        private void OnStateChanged(ServiceRuntimeState state)
        {
            if (_serviceMap.TryGetValue(state.Config.Id, out var vm))
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    () => vm.Update(state));
        }

        private int _logCount = 0;

        private static void Diag(string msg)
        {
            try
            {
                var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RollingUpdateManager", "diag.log");
                File.AppendAllText(f, $"[{DateTime.Now:HH:mm:ss.fff}] [VM] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        private void OnLogReceived(LogEntry entry)
        {
            int n = System.Threading.Interlocked.Increment(ref _logCount);
            if (n <= 5 || n % 100 == 0)
                Diag($"OnLogReceived #{n} serviceId={entry.ServiceId.Substring(0,8)} mapSize={_serviceMap.Count} found={_serviceMap.ContainsKey(entry.ServiceId)}");

            if (_serviceMap.TryGetValue(entry.ServiceId, out var vm))
            {
                // AddLog dispara NewLogLine → code-behind hace AppendText en Dispatcher.BeginInvoke
                // No necesitamos BeginInvoke aquí porque NewLogLine ya lo hace
                vm.AddLog(entry.ToString());
            }
        }
    }
}
