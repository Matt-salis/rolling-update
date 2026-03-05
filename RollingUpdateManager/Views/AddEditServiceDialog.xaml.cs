using System.Windows;
using Microsoft.Win32;
using RollingUpdateManager.Models;

namespace RollingUpdateManager.Views
{
    public partial class AddEditServiceDialog : Window
    {
        public ServiceConfig? ResultConfig { get; private set; }
        private readonly ServiceConfig? _existing;

        // ── Nuevo servicio ─────────────────────────────────────────────────────
        public AddEditServiceDialog()
        {
            InitializeComponent();
        }

        // ── Editar existente ───────────────────────────────────────────────────
        public AddEditServiceDialog(ServiceConfig config) : this()
        {
            _existing = config;
            Title = "Editar Servicio";

            TxtName.Text       = config.Name;
            TxtJarPath.Text    = config.JarPath;
            TxtConfigPath.Text = config.ConfigFilePath;
            TxtPublicPort.Text = config.PublicPort.ToString();
            TxtJvmArgs.Text    = config.JvmArguments;
            TxtJavaExe.Text    = config.JavaExecutable;
            TxtHealthPath.Text = config.HealthCheckPath;
            TxtTimeout.Text    = config.HealthCheckTimeoutSeconds.ToString();
            TxtDrainDelay.Text = config.DrainDelayMilliseconds.ToString();
            ChkAutoStart.IsChecked = config.AutoStart;
        }

        // ── Guardar ────────────────────────────────────────────────────────────
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtName.Text))
            {
                MessageBox.Show("El nombre del servicio es obligatorio.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtJarPath.Text))
            {
                MessageBox.Show("La ruta del JAR es obligatoria.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtPublicPort.Text, out var publicPort) || publicPort < 1 || publicPort > 65535)
            {
                MessageBox.Show("Puerto público inválido (1–65535).", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtHealthPath.Text) || !TxtHealthPath.Text.StartsWith("/"))
            {
                MessageBox.Show("La ruta de health-check debe comenzar con '/' (ej: /actuator/health).", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtTimeout.Text, out var timeout) || timeout < 5 || timeout > 600)
            {
                MessageBox.Show("Timeout de health-check inválido (5–600 segundos).", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var config = _existing ?? new ServiceConfig();
            config.Name                       = TxtName.Text.Trim();
            config.JarPath                    = TxtJarPath.Text.Trim();
            config.ConfigFilePath             = TxtConfigPath.Text.Trim();
            config.PublicPort                 = publicPort;
            config.JvmArguments               = TxtJvmArgs.Text.Trim();
            config.JavaExecutable             = string.IsNullOrWhiteSpace(TxtJavaExe.Text) ? "java" : TxtJavaExe.Text.Trim();
            config.HealthCheckPath            = TxtHealthPath.Text.Trim();
            config.HealthCheckTimeoutSeconds  = timeout;
            config.DrainDelayMilliseconds     = int.TryParse(TxtDrainDelay.Text, out var dd) && dd >= 0 ? dd : 3000;
            config.AutoStart                  = ChkAutoStart.IsChecked == true;

            ResultConfig   = config;
            DialogResult   = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;

        private void BrowseJar_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Title = "Seleccionar JAR", Filter = "JAR files (*.jar)|*.jar" };
            if (d.ShowDialog() == true) TxtJarPath.Text = d.FileName;
        }

        private void BrowseConfig_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog
            {
                Title  = "Seleccionar archivo de configuración",
                Filter = "Config files (*.properties;*.yml;*.yaml)|*.properties;*.yml;*.yaml|All files (*.*)|*.*"
            };
            if (d.ShowDialog() == true) TxtConfigPath.Text = d.FileName;
        }
    }
}
