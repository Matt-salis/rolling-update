using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RollingUpdateManager.Services;

namespace RollingUpdateManager.Infrastructure
{
    /// <summary>
    /// Host de Windows Service para el orquestador.
    /// Permite registrar la aplicación como servicio de Windows con:
    ///   sc create RollingUpdateManager binPath= "ruta\a\RollingUpdateManager.exe --service"
    ///
    /// O con NSSM (Non-Sucking Service Manager):
    ///   nssm install RollingUpdateManager "ruta\a\RollingUpdateManager.exe"
    /// </summary>
    public class WindowsServiceHost : BackgroundService
    {
        private readonly ServiceOrchestrator _orchestrator;
        private readonly ILogger<WindowsServiceHost> _logger;

        public WindowsServiceHost(
            ServiceOrchestrator orchestrator,
            ILogger<WindowsServiceHost> logger)
        {
            _orchestrator = orchestrator;
            _logger       = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("RollingUpdateManager iniciando…");
            try
            {
                await _orchestrator.InitializeAsync(stoppingToken);
                _logger.LogInformation("Orquestador inicializado. Esperando señal de parada…");

                // Mantener vivo hasta cancelación
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Apagado normal
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error crítico en el orquestador.");
            }
            finally
            {
                await _orchestrator.DisposeAsync();
                _logger.LogInformation("RollingUpdateManager detenido.");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers para instalar/desinstalar el servicio de Windows
    // ─────────────────────────────────────────────────────────────────────────
    public static class WindowsServiceInstaller
    {
        private const string ServiceName        = "RollingUpdateManager";
        private const string ServiceDisplayName = "MegaData Sistemas - Rolling Update Manager";
        private const string ServiceDescription = "Gestiona servicios JAR con estrategia Blue/Green sin downtime.";

        /// <summary>Instala el servicio con sc.exe.</summary>
        public static (bool success, string output) Install(string exePath)
        {
            var args = $"create {ServiceName} " +
                       $"binPath= \"{exePath} --service\" " +
                       $"DisplayName= \"{ServiceDisplayName}\" " +
                       $"start= auto";
            var (exit, out1) = RunSc(args);
            if (exit != 0) return (false, out1);

            // Añadir descripción
            RunSc($"description {ServiceName} \"{ServiceDescription}\"");

            // Configurar recovery: reiniciar en fallo
            RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");

            return (true, $"Servicio '{ServiceName}' instalado correctamente.");
        }

        /// <summary>Desinstala el servicio.</summary>
        public static (bool success, string output) Uninstall()
        {
            // Detener primero
            RunSc($"stop {ServiceName}");
            var (exit, out1) = RunSc($"delete {ServiceName}");
            return exit == 0
                ? (true,  $"Servicio '{ServiceName}' eliminado.")
                : (false, out1);
        }

        /// <summary>Inicia el servicio Windows.</summary>
        public static (int, string) Start()  => RunSc($"start {ServiceName}");

        /// <summary>Detiene el servicio Windows.</summary>
        public static (int, string) Stop()   => RunSc($"stop {ServiceName}");

        private static (int exitCode, string output) RunSc(string args)
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                Verb                   = "runas"    // requiere UAC elevado
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output.Trim());
        }
    }
}
