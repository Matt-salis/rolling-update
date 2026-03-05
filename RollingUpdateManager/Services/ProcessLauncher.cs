using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RollingUpdateManager.Infrastructure;
using RollingUpdateManager.Models;

namespace RollingUpdateManager.Services
{
    public class ProcessLauncher
    {
        public event Action<LogEntry>? LogReceived;

        private readonly PortManager _portManager;

        private static readonly string DiagFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RollingUpdateManager", "diag.log");

        // Canal de escritura asíncrona: los callers enolan mensajes sin tocar el disco.
        // Un único Task de fondo drena el canal y escribe en lote, eliminando el I/O
        // sincrónico del hot path de lectura de logs.
        private static readonly Channel<string> _diagChannel =
            Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
            {
                SingleReader          = true,
                FullMode              = BoundedChannelFullMode.DropOldest   // nunca bloquear callers
            });

        static ProcessLauncher()
        {
            // Drainer: lee del canal en lote y escribe al disco de forma asíncrona
            _ = Task.Run(async () =>
            {
                var sb = new StringBuilder(4096);
                var reader = _diagChannel.Reader;
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    sb.Clear();
                    // Drena todo lo disponible en memoria antes de ir al disco
                    while (reader.TryRead(out var msg))
                        sb.AppendLine(msg);

                    if (sb.Length == 0) continue;
                    try
                    {
                        // Rotación: si supera 5 MB, truncar a la mitad
                        if (File.Exists(DiagFile))
                        {
                            var info = new FileInfo(DiagFile);
                            if (info.Length > 5 * 1024 * 1024)
                            {
                                var content = File.ReadAllText(DiagFile);
                                File.WriteAllText(DiagFile, content.Substring(content.Length / 2));
                            }
                        }
                        await File.AppendAllTextAsync(DiagFile, sb.ToString()).ConfigureAwait(false);
                    }
                    catch { /* nunca crashear el drainer */ }
                }
            });
        }

        public static void Diag(string msg)
        {
            // Encolar sin bloquear — si el canal está lleno descarta el mensaje más antiguo
            _diagChannel.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        public ProcessLauncher(PortManager portManager)
        {
            _portManager = portManager;
            Diag("=== ProcessLauncher instanciado ===");
        }

        public ServiceInstance Start(ServiceConfig config, InstanceSlot slot)
        {
            var port = _portManager.AcquirePort(config.Id);
            var args = BuildJvmArguments(config, port);

            Diag($"START slot={slot} exe='{config.JavaExecutable}'");
            Diag($"  args='{args}'");
            Diag($"  JAR existe={File.Exists(config.JarPath)} path='{config.JarPath}'");

            var psi = new ProcessStartInfo
            {
                FileName               = config.JavaExecutable,
                Arguments              = args,
                WorkingDirectory       = Path.GetDirectoryName(config.JarPath) ?? ".",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding  = new UTF8Encoding(false),
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var instance = new ServiceInstance
            {
                ServiceId    = config.Id,
                Slot         = slot,
                InternalPort = port,
                Status       = ServiceStatus.Starting,
                JarPath      = config.JarPath,
                Process      = process
            };

            // Flag atómico para evitar doble liberación del puerto
            // (process.Exited puede competir con StopAsync)
            int _portReleased = 0;
            void ReleasePortOnce()
            {
                if (Interlocked.Exchange(ref _portReleased, 1) == 0)
                    _portManager.ReleasePort(port);
            }

            process.Exited += (_, _) =>
            {
                var code = SafeExitCode(process);
                Diag($"EXITED PID={instance.ProcessId} exitCode={code}");
                ReleasePortOnce();
                instance.Status = instance.Status == ServiceStatus.Running
                    ? ServiceStatus.Stopped
                    : ServiceStatus.Error;
                EmitLog(instance, "WARN", $"Proceso termino (exit code: {code})");
            };

            // Guardar la función para que StopAsync también la use
            instance.ReleasePort = ReleasePortOnce;

            bool started;
            try
            {
                started = process.Start();
                Diag($"process.Start()={started} PID={process.Id}");
            }
            catch (Exception ex)
            {
                Diag($"process.Start() EXCEPCION: {ex}");
                _portManager.ReleasePort(port);
                instance.Status = ServiceStatus.Error;
                EmitLog(instance, "ERROR", $"No se pudo iniciar el proceso: {ex.Message}");
                return instance;
            }

            instance.ProcessId = process.Id;
            instance.StartedAt = DateTime.UtcNow;

            // Asignar al Job Object: si el manager crashea, el kernel mata este proceso
            ProcessJobObject.Instance.Assign(process);

            StartReaderThread(process.StandardOutput, instance, isStderr: false);
            StartReaderThread(process.StandardError,  instance, isStderr: true);

            var launchMsg = $"Instancia {slot} iniciada -> PID {process.Id} | puerto interno: {port}";
            Diag(launchMsg);
            EmitLog(instance, "INFO", launchMsg);
            EmitLog(instance, "INFO", $"Comando: {config.JavaExecutable} {args}");

            return instance;
        }

        private void StartReaderThread(StreamReader reader, ServiceInstance instance, bool isStderr)
        {
            var streamName = isStderr ? "stderr" : "stdout";
            // Task async en lugar de Thread bloqueante: libera el thread del pool mientras
            // espera datos del pipe. Con 6 JARs esto ahorra ~12 threads de OS permanentes.
            _ = Task.Run(async () =>
            {
                Diag($"[{streamName}] reader arranco PID={instance.ProcessId}");
                int lineCount = 0;
                try
                {
                    while (true)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line is null)
                        {
                            Diag($"[{streamName}] EOF tras {lineCount} lineas");
                            break;
                        }
                        lineCount++;
                        if (lineCount <= 5 || lineCount % 50 == 0)
                            Diag($"[{streamName}] #{lineCount}: {line.Substring(0, Math.Min(120, line.Length))}");
                        if (line.Length == 0) continue;
                        var level = isStderr
                            ? (line.Contains(" ERROR ") ? "ERROR"
                               : line.Contains(" WARN  ") || line.Contains(" WARN ") ? "WARN"
                               : "INFO")
                            : "INFO";
                        EmitLog(instance, level, line);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    Diag($"[{streamName}] pipe cerrado: {ex.GetType().Name}");
                }
                catch (Exception ex)
                {
                    Diag($"[{streamName}] ERROR inesperado: {ex}");
                    EmitLog(instance, "ERROR", $"[Reader error] {ex.Message}");
                }
                Diag($"[{streamName}] reader termino");
            });
        }

        public async Task StopAsync(ServiceInstance instance, int gracefulMs = 5000)
        {
            if (instance.Process is null || instance.Process.HasExited)
            {
                instance.Status = ServiceStatus.Stopped;
                return;
            }
            Diag($"StopAsync PID={instance.ProcessId}");
            EmitLog(instance, "INFO", $"Deteniendo instancia {instance.Slot}…");
            try
            {
                // 1. Señal graceful primero: permite que Spring Boot haga flush de BD/caches.
                //    CloseMainWindow envía WM_CLOSE en Windows; el proceso Java la convierte
                //    en SIGTERM y activa los shutdown hooks registrados.
                bool closedGracefully = false;
                try
                {
                    closedGracefully = instance.Process.CloseMainWindow();
                }
                catch { /* proceso ya terminó o sin ventana */ }

                if (closedGracefully)
                {
                    // Esperar hasta 3s a que el proceso termine por sí solo
                    using var gracefulCts = new CancellationTokenSource(3000);
                    try { await instance.Process.WaitForExitAsync(gracefulCts.Token); }
                    catch (OperationCanceledException) { }
                }

                // 2. Si sigue vivo, forzar terminación
                if (!instance.Process.HasExited)
                {
                    instance.Process.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(gracefulMs);
                    try { await instance.Process.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { }
                }
            }
            catch (Exception ex)
            {
                EmitLog(instance, "ERROR", $"Error deteniendo proceso: {ex.Message}");
            }
            finally
            {
                instance.Status = ServiceStatus.Stopped;
                if (instance.ReleasePort is { } rel)
                    rel();
                else
                    _portManager.ReleasePort(instance.InternalPort);
            }
        }

        private static string BuildJvmArguments(ServiceConfig config, int internalPort)
        {
            var args = new List<string>
            {
                "-Dfile.encoding=UTF-8",
                "-Dstdout.encoding=UTF-8",
                "-Dspring.output.ansi.enabled=NEVER"
            };

            // JVM args opcionales (pueden estar vacíos)
            if (!string.IsNullOrWhiteSpace(config.JvmArguments))
                args.Add(config.JvmArguments);

            args.Add("-jar");
            args.Add($"\"{config.JarPath}\"");
            args.Add($"--server.port={internalPort}");

            // Config file opcional
            if (!string.IsNullOrWhiteSpace(config.ConfigFilePath))
                args.Add($"--spring.config.location=\"{config.ConfigFilePath}\"");

            return string.Join(" ", args);
        }

        private void EmitLog(ServiceInstance instance, string level, string message)
        {
            LogReceived?.Invoke(new LogEntry
            {
                Timestamp = DateTime.Now,
                ServiceId = instance.ServiceId,
                Slot      = instance.Slot,
                Level     = level,
                Message   = message
            });
        }

        private static int SafeExitCode(Process p)
        {
            try   { return p.ExitCode; }
            catch { return -1; }
        }
    }
}
