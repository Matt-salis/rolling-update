using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RollingUpdateManager.ViewModels;

namespace RollingUpdateManager.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private ServiceItemViewModel? _trackedService;

        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm         = vm;
            DataContext = vm;

            Loaded += async (_, _) => await _vm.InitializeAsync();

            // Cuando cambia el servicio seleccionado, reconectar listeners
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedService))
                    ReattachToService();
            };
        }

        private void ReattachToService()
        {
            // Desuscribir del servicio anterior
            if (_trackedService is not null)
            {
                _trackedService.NewLogLine  -= OnNewLogLine;
                _trackedService.PropertyChanged -= OnServicePropertyChanged;
            }

            _trackedService = _vm.SelectedService;

            if (_trackedService is not null)
            {
                // Cargar todo el texto acumulado hasta ahora
                LogTextBox.Text = _trackedService.LogText;
                LogScrollViewer.ScrollToBottom();

                // Suscribir para nuevas líneas
                _trackedService.NewLogLine      += OnNewLogLine;
                _trackedService.PropertyChanged += OnServicePropertyChanged;
            }
            else
            {
                LogTextBox.Text = string.Empty;
            }
        }

        // Línea nueva: AppendText es O(n_nueva_linea), no reconstruye todo el TextBox
        private void OnNewLogLine(string line)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                LogTextBox.AppendText(line);
                LogScrollViewer.ScrollToBottom();
            });
        }

        // LogText cambió (reset del buffer): reemplazar todo el texto
        private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ServiceItemViewModel.LogText))
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    LogTextBox.Text = _trackedService?.LogText ?? string.Empty;
                    LogScrollViewer.ScrollToBottom();
                });
            }
        }
    }
}

