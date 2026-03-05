using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RollingUpdateManager.Models;
using RollingUpdateManager.ViewModels;

namespace RollingUpdateManager.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private ServiceItemViewModel? _trackedService;
        private ToggleButton[]? _slotTabs;

        // CTS para cancelar cargas de texto pendientes al cambiar de tab/servicio.
        private CancellationTokenSource _loadCts = new();

        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm         = vm;
            DataContext = vm;

            Loaded += (_, _) =>
            {
                _slotTabs = new[] { TabAll, TabBlue, TabGreen };
                TabAll.IsChecked = true;
                _ = _vm.InitializeAsync();
            };

            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedService))
                    ReattachToService();
            };
        }

        private void SlotTabClicked(object sender, RoutedEventArgs e)
        {
            if (_trackedService is null || _slotTabs is null) return;
            var tab = (ToggleButton)sender;
            foreach (var t in _slotTabs)
                t.IsChecked = ReferenceEquals(t, tab);

            // Cambiar ViewingSlot dispara OnViewingSlotChanged → Interlocked.Increment(_viewGen)
            // → SlotViewChanged → OnSlotViewChanged() aquí abajo.
            // No llamamos LoadLogAsync aquí directamente: lo hace OnSlotViewChanged.
            _trackedService.ViewingSlot = tab.Tag switch
            {
                "Blue"  => InstanceSlot.Blue,
                "Green" => InstanceSlot.Green,
                _       => (InstanceSlot?)null
            };
        }

        private void ReattachToService()
        {
            if (_trackedService is not null)
            {
                _trackedService.NewLogLine      -= OnNewLogLine;
                _trackedService.SlotViewChanged -= OnSlotViewChanged;
            }

            _trackedService = _vm.SelectedService;

            if (_trackedService is not null)
            {
                _trackedService.NewLogLine      += OnNewLogLine;
                _trackedService.SlotViewChanged += OnSlotViewChanged;
                LoadLogAsync(_trackedService);
            }
            else
            {
                CancelAndClearLog();
            }
        }

        // Llamado cuando cambia el slot (tab Blue/Green/All).
        private void OnSlotViewChanged() => LoadLogAsync(_trackedService);

        /// <summary>
        /// Carga el texto del slot actual de forma asíncrona.
        /// Cancela cualquier carga pendiente antes de iniciar la nueva,
        /// y captura la generación (ViewGen) para descartar si llega otra
        /// mientras esta carga estaba en vuelo.
        /// </summary>
        private void LoadLogAsync(ServiceItemViewModel? svc)
        {
            // Cancelar carga anterior: evita TextBox.Text asignaciones apiladas
            var old = Interlocked.Exchange(ref _loadCts, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();

            if (svc is null) { CancelAndClearLog(); return; }

            var ct  = _loadCts.Token;
            var gen = svc.ViewGen;   // generación en el momento de iniciar la carga

            // Obtener el texto fuera del UI thread (GetCurrentLogText tiene lock interno)
            _ = Task.Run(() => svc.GetCurrentLogText(), ct)
                .ContinueWith(t =>
                {
                    if (t.IsCanceled || ct.IsCancellationRequested) return;
                    var text = t.Result;

                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        () =>
                        {
                            // Doble verificación: si el usuario cambió de tab mientras
                            // el Task.Run estaba corriendo, descartar el resultado.
                            if (ct.IsCancellationRequested) return;
                            if (svc.ViewGen != gen) return;

                            LogTextBox.Text = text;
                            LogScrollViewer.ScrollToBottom();
                        });
                }, ct, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
        }

        private void CancelAndClearLog()
        {
            var old = Interlocked.Exchange(ref _loadCts, new CancellationTokenSource());
            old.Cancel(); old.Dispose();
            LogTextBox.Text = string.Empty;
        }

        /// <summary>
        /// Línea nueva en tiempo real — solo se aplica si la generación coincide
        /// con la vista actual, descartando líneas del slot anterior que llegaron
        /// en tránsito durante un cambio de tab.
        /// </summary>
        private void OnNewLogLine(string line, int gen)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                // Si la generación ya no coincide, esta línea pertenece al slot
                // anterior y no debe mostrarse en el slot actual.
                if (_trackedService is null || _trackedService.ViewGen != gen) return;
                LogTextBox.AppendText(line);
                LogScrollViewer.ScrollToBottom();
            });
        }
    }
}


