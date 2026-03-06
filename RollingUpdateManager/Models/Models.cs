using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;

namespace RollingUpdateManager.Models
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  Estado del servicio visible en la UI
    // ─────────────────────────────────────────────────────────────────────────────
    public enum ServiceStatus
    {
        Stopped,
        Starting,
        Running,
        Updating,
        Error,
        Unhealthy
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Identificador de instancia Blue / Green
    // ─────────────────────────────────────────────────────────────────────────────
    public enum InstanceSlot
    {
        Blue,
        Green
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Configuración persistida de un servicio registrado
    // ─────────────────────────────────────────────────────────────────────────────
    public class ServiceConfig
    {
        /// <summary>Identificador único del servicio (GUID).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Nombre amigable mostrado en la UI.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Ruta al archivo .jar activo.</summary>
        public string JarPath { get; set; } = string.Empty;

        /// <summary>Ruta al archivo .properties o .yml (opcional).</summary>
        public string ConfigFilePath { get; set; } = string.Empty;

        /// <summary>Puerto público fijo expuesto al exterior.</summary>
        public int PublicPort { get; set; } = 8080;

        /// <summary>Argumentos JVM adicionales (ej: -Xmx512m).</summary>
        public string JvmArguments { get; set; } = string.Empty;

        /// <summary>Ruta al ejecutable java.exe (vacío = usar PATH).</summary>
        public string JavaExecutable { get; set; } = "java";

        /// <summary>Tiempo máximo (seg) para esperar health-check al iniciar.</summary>
        public int HealthCheckTimeoutSeconds { get; set; } = 60;

        /// <summary>Intervalo (seg) entre intentos de health-check.</summary>
        public int HealthCheckIntervalSeconds { get; set; } = 3;

        /// <summary>Endpoint de health-check (Spring Boot: /actuator/health).</summary>
        public string HealthCheckPath { get; set; } = "/actuator/health";

        /// <summary>Si true, la instancia arranca automáticamente con la app.</summary>
        public bool AutoStart { get; set; } = true;

        /// <summary>Delay (ms) entre health-check OK y apagado de la instancia vieja.</summary>
        public int DrainDelayMilliseconds { get; set; } = 3000;

        /// <summary>
        /// Timeout (seg) para que el backend devuelva los headers de respuesta.
        /// Cubre generación de PDFs y exports pesados. 0 = usar valor por defecto (240s).
        /// </summary>
        public int ProxyTimeoutSeconds { get; set; } = 240;

        /// <summary>
        /// Timeout (seg) para transferir el body completo de la respuesta al cliente.
        /// Debe ser menor que ProxyTimeoutSeconds. 0 = usar valor por defecto (230s).
        /// </summary>
        public int ProxyBodyTimeoutSeconds { get; set; } = 230;

        /// <summary>Fecha de creación.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Última vez que se actualizó el JAR.</summary>
        public DateTime? LastUpdatedAt { get; set; }

        /// <summary>Slot activo actualmente (Blue o Green).</summary>
        public InstanceSlot ActiveSlot { get; set; } = InstanceSlot.Blue;

        /// <summary>Historial de versiones de JAR desplegadas.</summary>
        public List<DeploymentRecord> DeploymentHistory { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Estado en tiempo de ejecución de una instancia (Blue o Green)
    // ─────────────────────────────────────────────────────────────────────────────
    public class ServiceInstance
    {
        public string ServiceId { get; set; } = string.Empty;
        public InstanceSlot Slot { get; set; }
        public int InternalPort { get; set; }
        public int ProcessId { get; set; }
        public ServiceStatus Status { get; set; } = ServiceStatus.Stopped;
        public DateTime? StartedAt { get; set; }
        public string JarPath { get; set; } = string.Empty;

        [JsonIgnore]
        public System.Diagnostics.Process? Process { get; set; }

        /// <summary>
        /// Delegate que libera el puerto exactamente una vez (compartido entre
        /// process.Exited y StopAsync para evitar doble liberación).
        /// </summary>
        [JsonIgnore]
        public Action? ReleasePort { get; set; }

        /// <summary>Tiempo en línea.</summary>
        public TimeSpan Uptime => StartedAt.HasValue
            ? DateTime.UtcNow - StartedAt.Value
            : TimeSpan.Zero;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Métricas del proxy en tiempo real (contadores thread-safe)
    // ─────────────────────────────────────────────────────────────────────────────
    public class ProxyMetrics
    {
        private long _totalRequests;
        private long _totalErrors;       // respuestas 502 / errores de proxy
        private long _totalLatencyMs;    // suma de latencias para calcular promedio

        // ── Ventana deslizante de 1 s para req/s ──────────────────────────────
        // Guardamos el conteo de la ventana anterior y el inicio de la ventana actual
        private long _windowRequests;
        private long _windowStartTicks = Environment.TickCount64;
        private double _lastReqPerSec;

        /// <summary>
        /// Registra una petición completada.
        /// Llamado desde el pipeline del proxy en cada respuesta (exitosa o error).
        /// </summary>
        public void RecordRequest(long latencyMs, bool isError)
        {
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Add(ref _totalLatencyMs, latencyMs);
            if (isError) Interlocked.Increment(ref _totalErrors);
            Interlocked.Increment(ref _windowRequests);
        }

        /// <summary>
        /// Devuelve un snapshot instantáneo de las métricas.
        /// Rota la ventana de req/s si han pasado ≥1 s.
        /// </summary>
        public (double ReqPerSec, double AvgLatencyMs, double ErrorPct) GetSnapshot()
        {
            long now = Environment.TickCount64;
            long elapsed = now - Interlocked.Read(ref _windowStartTicks);

            if (elapsed >= 1000)
            {
                long reqs = Interlocked.Exchange(ref _windowRequests, 0);
                // Fijar nueva ventana
                Interlocked.Exchange(ref _windowStartTicks, now);
                _lastReqPerSec = elapsed > 0 ? reqs * 1000.0 / elapsed : 0;
            }

            long total  = Interlocked.Read(ref _totalRequests);
            long errors = Interlocked.Read(ref _totalErrors);
            long latSum = Interlocked.Read(ref _totalLatencyMs);

            double avgLat  = total > 0 ? (double)latSum / total : 0;
            double errPct  = total > 0 ? errors * 100.0 / total : 0;

            return (_lastReqPerSec, avgLat, errPct);
        }

        /// <summary>Devuelve el total de peticiones procesadas desde el inicio.</summary>
        public long TotalRequests => Interlocked.Read(ref _totalRequests);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Estado completo en tiempo de ejecución de un servicio registrado
    // ─────────────────────────────────────────────────────────────────────────────
    public class ServiceRuntimeState
    {
        public ServiceConfig Config { get; set; } = new();
        public ServiceInstance? Blue { get; set; }
        public ServiceInstance? Green { get; set; }
        public ServiceStatus OverallStatus { get; set; } = ServiceStatus.Stopped;
        public string LastError { get; set; } = string.Empty;

        /// <summary>Métricas en tiempo real del proxy de este servicio.</summary>
        public ProxyMetrics Metrics { get; } = new();

        /// <summary>
        /// Marca de tiempo desde la que el servicio corre de forma continua
        /// sin redeploy, restart manual ni cambio de slot.
        /// Se fija al pasar a Running y se resetea en cada RollingUpdate o RestartAsync.
        /// El watchdog NO la resetea: un auto-restart transparente no rompe el contrato
        /// de "mismo JAR, mismo slot" desde el punto de vista del operador.
        /// null = servicio nunca ha llegado a Running desde que abrimos la app.
        /// </summary>
        public DateTime? StableUptimeSince { get; set; }

        /// <summary>Instancia activa (la que recibe tráfico del proxy).</summary>
        public ServiceInstance? ActiveInstance =>
            Config.ActiveSlot == InstanceSlot.Blue ? Blue : Green;

        /// <summary>Instancia standby (parada o en warm-up).</summary>
        public ServiceInstance? StandbyInstance =>
            Config.ActiveSlot == InstanceSlot.Blue ? Green : Blue;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Registro histórico de despliegues
    // ─────────────────────────────────────────────────────────────────────────────
    public class DeploymentRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string JarPath { get; set; } = string.Empty;
        public string JarFileName { get; set; } = string.Empty;
        public InstanceSlot DeployedToSlot { get; set; }
        public DateTime DeployedAt { get; set; } = DateTime.UtcNow;
        public bool Success { get; set; }
        public string Notes { get; set; } = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Modelo global persistido en data/services.json
    // ─────────────────────────────────────────────────────────────────────────────
    public class AppData
    {
        public List<ServiceConfig> Services { get; set; } = new();
        public PortRangeConfig PortRanges { get; set; } = new();
        public DateTime LastSaved { get; set; } = DateTime.UtcNow;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Configuración de rangos de puertos internos
    // ─────────────────────────────────────────────────────────────────────────────
    public class PortRangeConfig
    {
        public int RangeStart { get; set; } = 10000;
        public int RangeEnd   { get; set; } = 19999;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Handoff: estado que el exe viejo pasa al nuevo durante una auto-actualización
    //  Se persiste en handoff.json y permite que el nuevo exe adopte procesos vivos
    //  sin reiniciarlos (zero-downtime para los consumers de los servicios).
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Estado de una instancia Java viva que el nuevo exe debe adoptar.
    /// </summary>
    public class HandoffInstance
    {
        public string        ServiceId    { get; set; } = string.Empty;
        public InstanceSlot  Slot         { get; set; }
        public int           ProcessId    { get; set; }
        public int           InternalPort { get; set; }
        public bool          IsActive     { get; set; }   // true = recibe tráfico del proxy
        public DateTime      StartedAt    { get; set; }
        public string        JarPath      { get; set; } = string.Empty;
    }

    /// <summary>
    /// Paquete completo de handoff escrito por el exe saliente.
    /// El exe entrante lo lee al arrancar y, si existe, llama a ReattachFromHandoffAsync
    /// en lugar del ciclo AutoStart normal.
    /// </summary>
    public class HandoffState
    {
        public DateTime             WrittenAt  { get; set; } = DateTime.UtcNow;
        public List<HandoffInstance> Instances { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Línea de log con metadatos
    // ─────────────────────────────────────────────────────────────────────────────
    public class LogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string ServiceId  { get; set; } = string.Empty;
        public InstanceSlot Slot { get; set; }
        public string Level      { get; set; } = "INFO";   // INFO | WARN | ERROR
        public string Message    { get; set; } = string.Empty;

        public override string ToString() =>
            $"[{Timestamp:HH:mm:ss}] [{Level,-5}] {Message}";
    }
}
