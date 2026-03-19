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
            svc.AddSingleton<HandoffService>();
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
                    services.AddSingleton<HandoffService>();
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
        /// Actualiza el exe en caliente con zero-downtime para los servicios Java:
        ///   1. DetachForHandoffAsync: escribe handoff.json y desactiva KILL_ON_JOB_CLOSE.
        ///   2. Lanza bat que espera la muerte del proceso actual (waitfor), hace el swap y arranca el nuevo exe.
        ///   3. Cierra esta instancia — DisposeAsync en modo handoff solo cierra los proxies Kestrel,
        ///      los java.exe sobreviven y el nuevo exe los adopta via handoff.json.
        ///
        /// NOTA: este método se llama desde el UI thread. El DetachForHandoffAsync corre en
        /// un thread separado para no bloquear el dispatcher, y usamos ConfigureAwait(false)
        /// + GetAwaiter().GetResult() para evitar deadlock en WPF.
        /// </summary>
        public static void ScheduleUpdateAndRestart(string newExePath)
        {
            var current = Process.GetCurrentProcess().MainModule!.FileName;
            var dir     = Path.GetDirectoryName(current)!;
            var newCopy = Path.Combine(dir, "RollingUpdateManager_new.exe");
            var oldCopy = Path.Combine(dir, "RollingUpdateManager_old.exe");

            // Copiar el nuevo exe junto al actual
            File.Copy(newExePath, newCopy, overwrite: true);

            // El bat espera a que el proceso ACTUAL muera (usando su PID), luego hace el swap.
            // Ventana visible para que cualquier error sea evidente al usuario.
            // Grace de 2 s antes del move para que el AV termine de escanear el nuevo exe.
            // Retry loop para el move por si el AV mantiene el lock un momento extra.
            int currentPid = Process.GetCurrentProcess().Id;
            var script = $"""
                @echo off
                echo [RUM] Esperando cierre de la aplicacion ({currentPid})...
                :waitloop
                tasklist /FI "PID eq {currentPid}" 2>NUL | find "{currentPid}" >NUL
                if not errorlevel 1 (
                    timeout /t 1 /nobreak >nul
                    goto waitloop
                )
                echo [RUM] Proceso terminado. Esperando 2 s para que el antivirus libere el archivo...
                timeout /t 2 /nobreak >nul
                echo [RUM] Instalando nueva version...
                set /a tries=0
                :moveloop
                set /a tries=%tries%+1
                if %tries% gtr 15 (
                    echo [RUM] ERROR: No se pudo renombrar el ejecutable despues de 15 intentos.
                    echo Cierra el antivirus temporalmente o ejecuta como administrador.
                    pause
                    exit /b 1
                )
                move /Y "{current}" "{oldCopy}" 2>NUL
                if errorlevel 1 ( timeout /t 1 /nobreak >nul & goto moveloop )
                move /Y "{newCopy}" "{current}"
                if errorlevel 1 (
                    echo [RUM] ERROR al mover nuevo exe. Restaurando version anterior...
                    move /Y "{oldCopy}" "{current}"
                    pause
                    exit /b 1
                )
                echo [RUM] Iniciando nueva version...
                start /d "{dir}" "" "{current}"
                exit /b 0
                """;

            var batPath = Path.Combine(Path.GetTempPath(), "rum_update.bat");
            File.WriteAllText(batPath, script, System.Text.Encoding.ASCII);

            // Fase 1: handoff — escribe handoff.json y desactiva KILL_ON_JOB_CLOSE.
            // Se corre en ThreadPool para evitar deadlock WPF (UI thread no puede awaitar
            // directamente tareas que completan en el UI thread).
            if (Current is App app && app._services is { } sp)
            {
                var orchestrator = sp.GetService<ServiceOrchestrator>();
                if (orchestrator is not null)
                {
                    try
                    {
                        // ConfigureAwait(false) garantiza que la continuación no vuelva
                        // al UI thread (que está bloqueado en .Wait()), evitando deadlock.
                        Task.Run(() => orchestrator.DetachForHandoffAsync())
                            .Wait(TimeSpan.FromSeconds(5));
                    }
                    catch { /* si falla, el nuevo exe hará AutoStart normal */ }
                }
            }

            // Fase 2: lanzar el bat de swap (el bat espera la muerte del proceso actual).
            // UseShellExecute=true abre una ventana cmd visible: si algo falla, el usuario lo ve.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{batPath}\"")
            {
                UseShellExecute = true   // ventana visible — errores obvios al usuario
            });

            // Fase 3: cerrar esta instancia.
            // DisposeAsync con _handoffMode=true solo cierra los proxies Kestrel (~ms).
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_services is IAsyncDisposable ad)
            {
                var orchestrator = _services.GetService<ServiceOrchestrator>();
                bool isHandoff   = orchestrator?.IsHandoffMode ?? false;

                // Cierre normal (no swap de exe): guardar estado persistente para que los
                // servicios Java continúen corriendo y la app los readopte al volver a abrirse.
                // DetachForHandoffAsync escribe handoff.json (IsPersistent=true) y llama
                // ProcessJobObject.DetachAll() para que los java.exe sobrevivan a nuestra muerte.
                // Si falla (disco lleno, permisos, etc.) los procesos serán matados por el
                // Job Object — comportamiento degradado aceptable.
                if (!isHandoff && orchestrator is not null)
                {
                    try
                    {
                        Task.Run(() => orchestrator.DetachForHandoffAsync(persistent: true))
                            .Wait(TimeSpan.FromSeconds(5));
                        isHandoff = true;  // DisposeAsync solo cerrará los proxies Kestrel
                    }
                    catch { /* si falla, DisposeAsync matará los procesos normalmente */ }
                }

                // En modo handoff (update o cierre normal con state guardado) solo cerramos
                // los proxies Kestrel: operación rápida (~ms).
                // En modo normal (fallo al guardar state) matamos todos los procesos Java.
                var timeout = isHandoff ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(15);

                try
                {
                    Task.Run(async () => await ad.DisposeAsync())
                        .Wait(timeout);
                }
                catch { /* timeout o error — el Job Object matará los procesos si no es handoff */ }
            }
            base.OnExit(e);
        }
    }
}
