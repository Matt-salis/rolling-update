using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RollingUpdateManager.Services;

namespace RollingUpdateManager.Services
{
    /// <summary>
    /// Verifica la salud de una instancia Spring Boot recién iniciada.
    /// Estrategia: HTTP Actuator → TCP port-open (fallback)
    /// Abort rápido si el proceso muere durante la espera.
    /// </summary>
    public class HealthCheckService
    {
        // Fábrica de handlers: cada WaitForReadyAsync crea su propio HttpClient.
        // Con 6 health-checks en paralelo sobre un solo cliente estático, las conexiones
        // del pool se agotan y los checks se cuelgan esperando socket libre.
        private static HttpClient MakeClient() => new(
            new SocketsHttpHandler
            {
                ConnectTimeout           = TimeSpan.FromSeconds(3),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                // Un único intento de conexión por check: no reintentar a nivel de socket
                EnableMultipleHttp2Connections = false,
            })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        // ── HTTP health-check ──────────────────────────────────────────────────
        public async Task<bool> WaitForHealthAsync(
            int    port,
            string path,
            int    timeoutSeconds  = 60,
            int    intervalSeconds = 3,
            Process? process       = null,
            CancellationToken ct   = default)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            var url      = $"http://localhost:{port}{path}";
            using var http = MakeClient();

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (process is not null && process.HasExited)
                    return false;

                try
                {
                    var response = await http.GetAsync(url, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        if (IsUp(body, response.IsSuccessStatusCode)) return true;
                    }
                }
                catch
                {
                    // Aún no disponible; reintentamos
                }

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }

            return false;
        }

        // ── TCP port-open (fallback) ───────────────────────────────────────────────
        public async Task<bool> WaitForPortAsync(
            int    port,
            int    timeoutSeconds  = 60,
            int    intervalSeconds = 3,
            Process? process       = null,
            CancellationToken ct   = default)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (process is not null && process.HasExited)
                    return false;

                if (PortManager.IsPortOpen("localhost", port, timeoutMs: 500))
                    return true;

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }

            return false;
        }

        // ── Estrategia combinada ─────────────────────────────────────────────────
        /// <summary>
        /// Intenta HTTP Actuator con el timeout completo.
        /// Si pasa el 50% del tiempo sin éxito, cae a TCP port-open con el tiempo restante.
        /// Abort inmediato si el proceso muere.
        /// </summary>
        public async Task<bool> WaitForReadyAsync(
            int    port,
            string healthPath,
            int    timeoutSeconds  = 60,
            int    intervalSeconds = 3,
            Process? process       = null,
            CancellationToken ct   = default)
        {
            // Dar el timeout completo al HTTP actuator.
            // Antes se dividía a la mitad (Math.Min(t,15)) dejando solo 15s para JARs pesados.
            // El fallback TCP solo se usa si el HTTP no responde Y el proceso sigue vivo.
            var httpOk = await WaitForHealthAsync(
                port, healthPath, timeoutSeconds, intervalSeconds, process, ct);
            if (httpOk) return true;

            // Si el proceso ya murió, no hay nada que esperar
            if (process is not null && process.HasExited) return false;

            // Fallback TCP: el JAR puede estar corriendo pero sin actuator configurado.
            // Usar un timeout corto (10s o lo que quede si ya casi se agotó).
            int tcpTimeout = Math.Min(10, intervalSeconds * 3);
            return await WaitForPortAsync(port, tcpTimeout, intervalSeconds, process, ct);
        }

        // ── Parser de respuesta ──────────────────────────────────────────────────────
        private static bool IsUp(string json, bool isSuccessStatus)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("status", out var statusProp))
                {
                    var status = statusProp.GetString();
                    // Aceptar explícitamente solo "UP".
                    // "UNKNOWN" y "OUT_OF_SERVICE" son estados de Spring Boot que indican
                    // que la app arrancó pero aún no está lista para recibir tráfico.
                    return string.Equals(status, "UP", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { /* no es JSON válido */ }

            // Si responde 200 pero NO tiene campo "status" (no es endpoint actuator)
            // lo aceptamos como UP solo si el código fue 2xx.
            return isSuccessStatus;
        }
    }
}

