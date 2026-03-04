using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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
    //  Estado completo en tiempo de ejecución de un servicio registrado
    // ─────────────────────────────────────────────────────────────────────────────
    public class ServiceRuntimeState
    {
        public ServiceConfig Config { get; set; } = new();
        public ServiceInstance? Blue { get; set; }
        public ServiceInstance? Green { get; set; }
        public ServiceStatus OverallStatus { get; set; } = ServiceStatus.Stopped;
        public string LastError { get; set; } = string.Empty;

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
