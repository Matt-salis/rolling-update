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
        // HttpClient con pool propio: no comparte conexiones entre servicios distintos
        private static readonly HttpClient _http = new(
            new SocketsHttpHandler
            {
                ConnectTimeout           = TimeSpan.FromSeconds(3),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
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

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                // Abort rápido: si el proceso murió, no tiene sentido seguir esperando
                if (process is not null && process.HasExited)
                    return false;

                try
                {
                    var response = await _http.GetAsync(url, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        if (IsUp(body)) return true;
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
            // Usar la mitad del timeout para HTTP (da tiempo al Spring Boot de arrancar)
            int httpTimeout = Math.Max(timeoutSeconds / 2, Math.Min(timeoutSeconds, 15));
            var httpOk = await WaitForHealthAsync(port, healthPath, httpTimeout, intervalSeconds, process, ct);
            if (httpOk) return true;

            // Si el proceso ya murió, no hay nada que esperar
            if (process is not null && process.HasExited) return false;

            // Fallback con el tiempo restante
            int remaining = timeoutSeconds - httpTimeout;
            if (remaining <= 0) return false;
            return await WaitForPortAsync(port, remaining, intervalSeconds, process, ct);
        }

        // ── Parser de respuesta ──────────────────────────────────────────────────────
        private static bool IsUp(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("status", out var statusProp))
                    return statusProp.GetString()?.Equals("UP", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch { /* no es JSON válido */ }

            // Si responde 200 pero no tiene "status", lo consideramos UP
            return true;
        }
    }
}

