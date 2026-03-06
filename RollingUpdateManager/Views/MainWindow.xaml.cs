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

        // Si el ScrollViewer estaba al final antes del flush, auto-scrolleamos.
        // Si el usuario subió para ver logs anteriores, NO lo movemos.
        private bool _autoScroll = true;

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

            // Detectar si el usuario scrolleó manualmente hacia arriba
            LogScrollViewer.ScrollChanged += (_, e) =>
            {
                // Si el cambio fue causado por el contenido creciendo (ExtentHeight cambió)
                // y el usuario NO lo provocó, no cambiamos _autoScroll.
                // Si el usuario scrolleó (VerticalOffset cambió pero no por append), lo detectamos.
                if (e.ExtentHeightChange == 0)
                {
                    // El usuario movió el scroll
                    _autoScroll = LogScrollViewer.VerticalOffset >=
                                  LogScrollViewer.ScrollableHeight - 2;
                }
            };
        }

        private void SlotTabClicked(object sender, RoutedEventArgs e)
        {
            if (_trackedService is null || _slotTabs is null) return;
            var tab = (ToggleButton)sender;
            foreach (var t in _slotTabs)
                t.IsChecked = ReferenceEquals(t, tab);

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
                _trackedService.PendingLogReady -= OnPendingLogReady;
                _trackedService.SlotViewChanged -= OnSlotViewChanged;
            }

            _trackedService = _vm.SelectedService;

            if (_trackedService is not null)
            {
                _trackedService.PendingLogReady += OnPendingLogReady;
                _trackedService.SlotViewChanged += OnSlotViewChanged;
                _autoScroll = true;
                _textBoxLineCount = 0;
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
        /// Carga el texto completo del slot actual de forma asíncrona.
        /// Se llama al cambiar de servicio o de slot (tab).
        /// El timer de flush (50ms) se encargará del streaming en tiempo real.
        /// </summary>
        private void LoadLogAsync(ServiceItemViewModel? svc)
        {
            // Cancelar carga anterior
            var old = Interlocked.Exchange(ref _loadCts, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();

            if (svc is null) { CancelAndClearLog(); return; }

            var ct  = _loadCts.Token;
            var gen = svc.ViewGen;

            _ = Task.Run(() => svc.GetCurrentLogText(), ct)
                .ContinueWith(t =>
                {
                    if (t.IsCanceled || ct.IsCancellationRequested) return;
                    var text = t.Result;

                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        () =>
                        {
                            if (ct.IsCancellationRequested) return;
                            if (svc.ViewGen != gen) return;

                            LogTextBox.Text = text;
                            _textBoxLineCount = CountLines(text);
                            _autoScroll = true;
                            LogScrollViewer.ScrollToBottom();
                        });
                }, ct, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
        }

        private void CancelAndClearLog()
        {
            var old = Interlocked.Exchange(ref _loadCts, new CancellationTokenSource());
            old.Cancel(); old.Dispose();
            LogTextBox.Text = string.Empty;
            _textBoxLineCount = 0;
        }

        // Cuántas líneas máximo mantener en el TextBox visible.
        // WPF TextBox hace layout O(n) por cada AppendText cuando el texto crece;
        // con 2000 líneas y logs rápidos el UI thread se satura midiendo caracteres.
        // Al superar este umbral hacemos un trim que descarta las primeras N/2 líneas.
        private const int MaxTextBoxLines = 500;
        private int _textBoxLineCount;

        /// <summary>
        /// Recibe el texto batch acumulado en 50 ms y lo aplica al TextBox.
        /// Solo hace ScrollToBottom si el usuario ya estaba al final.
        /// Llamado en el UI thread por el _logFlushTimer de MainViewModel.
        /// </summary>
        private void OnPendingLogReady(string text)
        {
            if (_trackedService is null) return;

            // Contar cuántas líneas vienen en el batch
            int newLines = CountLines(text);
            _textBoxLineCount += newLines;

            // Si el TextBox acumuló demasiado texto, hacer trim para mantener rendimiento.
            // WPF TextBox.Text = string es O(n) pero lo hacemos solo cuando necesario.
            if (_textBoxLineCount > MaxTextBoxLines)
            {
                var current = LogTextBox.Text;
                var trimmed = TailOfString(current, MaxTextBoxLines / 2);
                LogTextBox.Text = trimmed;
                _textBoxLineCount = MaxTextBoxLines / 2;
            }

            LogTextBox.AppendText(text);

            if (_autoScroll)
                LogScrollViewer.ScrollToBottom();
        }

        private static int CountLines(string s)
        {
            if (s.Length == 0) return 0;
            int n = 0;
            foreach (char c in s)
                if (c == '\n') n++;
            return n;
        }

        private static string TailOfString(string s, int maxLines)
        {
            if (s.Length == 0) return s;
            int newlines = 0;
            int pos = s.Length - 1;
            if (s[pos] == '\n') pos--;
            while (pos >= 0)
            {
                if (s[pos] == '\n') { newlines++; if (newlines == maxLines) { pos++; break; } }
                pos--;
            }
            return pos <= 0 ? s : s.Substring(pos);
        }
    }
}



