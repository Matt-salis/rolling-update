using System;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RollingUpdateManager.Infrastructure;
using RollingUpdateManager.Proxy;
using RollingUpdateManager.Services;
using RollingUpdateManager.ViewModels;
using RollingUpdateManager.Views;

namespace RollingUpdateManager
{
    public partial class App : Application
    {
        private IServiceProvider? _services;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ── Modo servicio de Windows (argumento --service) ─────────────────
            if (e.Args.Contains("--service"))
            {
                RunAsWindowsService();
                return;
            }

            // ── Comandos de instalación / desinstalación ───────────────────────
            if (e.Args.Contains("--install"))
            {
                var path = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
                var (ok, msg) = WindowsServiceInstaller.Install(path);
                MessageBox.Show(msg, ok ? "Instalado" : "Error",
                    MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                Shutdown();
                return;
            }

            if (e.Args.Contains("--uninstall"))
            {
                var (ok, msg) = WindowsServiceInstaller.Uninstall();
                MessageBox.Show(msg, ok ? "Desinstalado" : "Error",
                    MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                Shutdown();
                return;
            }

            // ── Modo GUI normal ────────────────────────────────────────────────
            _services = BuildServices();

            var mainVm     = _services.GetRequiredService<MainViewModel>();
            var mainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow     = mainWindow;
            mainWindow.Show();
        }

        // ── Contenedor DI ──────────────────────────────────────────────────────
        private static IServiceProvider BuildServices()
        {
            var svc = new ServiceCollection();

            // Infraestructura
            svc.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
            svc.AddSingleton<PersistenceService>();
            svc.AddSingleton<PortManager>();
            svc.AddSingleton<ProcessLauncher>();
            svc.AddSingleton<HealthCheckService>();
            svc.AddSingleton<ProxyManager>();
            svc.AddSingleton<ServiceOrchestrator>();

            // Presentación
            svc.AddSingleton<MainViewModel>();
            svc.AddSingleton<MainWindow>();

            return svc.BuildServiceProvider();
        }

        // ── Modo servicio Windows ──────────────────────────────────────────────
        private static void RunAsWindowsService()
        {
            Microsoft.Extensions.Hosting.Host
                .CreateDefaultBuilder()
                .UseWindowsService()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<PersistenceService>();
                    services.AddSingleton<PortManager>();
                    services.AddSingleton<ProcessLauncher>();
                    services.AddSingleton<HealthCheckService>();
                    services.AddSingleton<ProxyManager>();
                    services.AddSingleton<ServiceOrchestrator>();
                    services.AddHostedService<WindowsServiceHost>();
                })
                .Build()
                .Run();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Shutdown sincrónico con timeout de 15 s para matar todos los procesos hijos.
            // NO se puede usar async void con await real aquí porque el proceso puede
            // terminar antes de que el await se complete.
            if (_services is IAsyncDisposable ad)
            {
                try
                {
                    Task.Run(async () => await ad.DisposeAsync())
                        .Wait(TimeSpan.FromSeconds(15));
                }
                catch { /* timeout o error — el Job Object matará los procesos de todas formas */ }
            }
            base.OnExit(e);
        }
    }
}
