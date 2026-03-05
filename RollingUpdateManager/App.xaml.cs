using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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

            // ── Auto-update: si existe un _new.exe junto a nosotros, somos el nuevo. ──
            // El exe viejo ya fue renombrado a _old.exe por el script de swap;
            // limpiamos el _old por si quedara de una actualización previa.
            ApplyPendingUpdate();

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
            // Precalentar el thread pool: en un VPS con pocos vCPUs el pool arranca
            // pequeño y tarda segundos en escalar. Con 6 JARs esto causa inanición.
            {
                int min = Math.Max(32, Environment.ProcessorCount * 8);
                ThreadPool.SetMinThreads(min, min);
            }

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

        // ── Auto-update helpers ───────────────────────────────────────

        /// <summary>
        /// Limpia restos de actualizaciones anteriores (_old.exe).
        /// Llamado al inicio de cada arranque.
        /// </summary>
        private static void ApplyPendingUpdate()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule!.FileName;
                var dir     = Path.GetDirectoryName(exePath)!;
                var oldExe  = Path.Combine(dir, "RollingUpdateManager_old.exe");
                if (File.Exists(oldExe))
                    File.Delete(oldExe);
            }
            catch { /* no es crítico */ }
        }

        /// <summary>
        /// Copia <paramref name="newExePath"/> junto al exe actual como _new.exe,
        /// lanza un script cmd que espera 2 s (mientras este proceso cierra),
        /// renombra el exe actual a _old.exe, renombra _new.exe al nombre definitivo
        /// y arranca la nueva versión.
        /// </summary>
        public static void ScheduleUpdateAndRestart(string newExePath)
        {
            var current = Process.GetCurrentProcess().MainModule!.FileName;
            var dir     = Path.GetDirectoryName(current)!;
            var exeName = Path.GetFileName(current);
            var newCopy = Path.Combine(dir, "RollingUpdateManager_new.exe");
            var oldCopy = Path.Combine(dir, "RollingUpdateManager_old.exe");

            // Copiar el nuevo exe junto al actual
            File.Copy(newExePath, newCopy, overwrite: true);

            // Script de swap: espera a que el proceso actual cierre, hace el swap y arranca
            // Usamos variables con comillas para manejar rutas con espacios.
            var script = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                move /Y "{current}" "{oldCopy}"
                move /Y "{newCopy}" "{current}"
                start "" "{current}"
                """;

            var batPath = Path.Combine(Path.GetTempPath(), "rum_update.bat");
            File.WriteAllText(batPath, script);

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{batPath}\"")
            {
                CreateNoWindow  = true,
                UseShellExecute = false
            });

            // Cerrar la aplicación para liberar el exe
            Current.Shutdown();
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
