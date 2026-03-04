using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RollingUpdateManager.Proxy
{
    /// <summary>
    /// Proxy inverso embebido en la aplicación.
    /// Cada servicio registrado escucha en su <b>puerto público fijo</b> y reenvía
    /// tráfico al puerto interno de la instancia activa.
    ///
    /// Cambio de target: llama a <see cref="UpdateTarget"/> → no reinicia el servidor.
    ///
    /// Implementación: reverse-proxy manual con HttpClient (no requiere YARP completo
    /// para el escenario de un único backend por listener).
    /// </summary>
    public class ProxyManager : IAsyncDisposable
    {
        // serviceId → ProxyEntry
        private readonly ConcurrentDictionary<string, ProxyEntry> _entries = new();

        // ── Registrar / actualizar un servicio en el proxy ─────────────────────
        /// <summary>
        /// Crea un listener en <paramref name="publicPort"/> para el servicio indicado.
        /// Si ya existía, solo actualiza el target interno.
        /// </summary>
        public async Task EnsureProxyAsync(
            string serviceId,
            int    publicPort,
            int    internalPort,
            CancellationToken ct = default)
        {
            if (_entries.TryGetValue(serviceId, out var existing))
            {
                existing.UpdateTarget(internalPort);
                return;
            }

            var entry = new ProxyEntry(serviceId, publicPort, internalPort);
            await entry.StartAsync(ct);
            _entries[serviceId] = entry;
        }

        /// <summary>
        /// Cambia dinámicamente el backend de la instancia activa SIN reiniciar el listener.
        /// Esta es la operación clave del rolling update.
        /// </summary>
        public void UpdateTarget(string serviceId, int newInternalPort)
        {
            if (_entries.TryGetValue(serviceId, out var entry))
                entry.UpdateTarget(newInternalPort);
        }

        /// <summary>
        /// Obtiene el puerto interno al que apunta actualmente el proxy.
        /// </summary>
        public int? GetCurrentTarget(string serviceId) =>
            _entries.TryGetValue(serviceId, out var e) ? e.CurrentTarget : null;

        /// <summary>
        /// Detiene el listener del proxy para un servicio (ej: al eliminar el servicio).
        /// </summary>
        public async Task RemoveProxyAsync(string serviceId)
        {
            if (_entries.TryRemove(serviceId, out var entry))
                await entry.StopAsync();
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────
        public async ValueTask DisposeAsync()
        {
            foreach (var entry in _entries.Values)
                await entry.StopAsync();
            _entries.Clear();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Entrada individual de proxy: un host ASP.NET Core mínimo por puerto público
    // ═══════════════════════════════════════════════════════════════════════════
    internal sealed class ProxyEntry : IAsyncDisposable
    {
        private readonly string _serviceId;
        private readonly int    _publicPort;
        private volatile int    _currentTarget;   // puerto interno actual (volatile para cambio atómico)

        private IHost?      _host;
        private HttpClient? _httpClient;  // un único client/handler; reutilizado entre updates

        public int CurrentTarget => _currentTarget;

        public ProxyEntry(string serviceId, int publicPort, int initialTarget)
        {
            _serviceId     = serviceId;
            _publicPort    = publicPort;
            _currentTarget = initialTarget;
        }

        // ── Cambio dinámico de target (thread-safe) ────────────────────────────
        // Solo actualiza el volatile _currentTarget; la pipeline lee el valor en
        // cada petición, por lo que el cambio es instantáneo sin recrear nada.
        // No se crea un nuevo HttpClient (evita acumulación de connection pools).
        public void UpdateTarget(int newTarget) =>
            Interlocked.Exchange(ref _currentTarget, newTarget);

        // ── Arrancar listener ──────────────────────────────────────────────────
        public async Task StartAsync(CancellationToken ct = default)
        {
            // Un solo handler con pool de conexiones que dura toda la vida del proxy.
            // Las peticiones construyen la URL con _currentTarget en cada llamada.
            _httpClient = new HttpClient(
                new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    ConnectTimeout           = TimeSpan.FromSeconds(5)
                });

            _host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(web =>
                {
                    web.UseKestrel(opts =>
                    {
                        opts.Listen(IPAddress.Any, _publicPort);
                    });
                    web.Configure(ConfigurePipeline);
                })
                .Build();

            await _host.StartAsync(ct);
        }

        // ── Pipeline de reenvío ────────────────────────────────────────────────
        private void ConfigurePipeline(IApplicationBuilder app)
        {
            app.Run(async context =>
            {
                var targetPort = _currentTarget;
                var client     = _httpClient!;

                // Construir la URL destino
                var targetUri = new UriBuilder
                {
                    Scheme = "http",
                    Host   = "localhost",
                    Port   = targetPort,
                    Path   = context.Request.Path,
                    Query  = context.Request.QueryString.Value ?? string.Empty
                }.Uri;

                // Clonar la petición entrante
                using var reqMsg = new HttpRequestMessage
                {
                    Method     = new HttpMethod(context.Request.Method),
                    RequestUri = targetUri
                };

                // Copiar headers (excepto los de hop-by-hop)
                foreach (var header in context.Request.Headers)
                {
                    if (HopByHopHeaders.Contains(header.Key)) continue;
                    if (!reqMsg.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value.ToArray()))
                        reqMsg.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value.ToArray());
                }

                // Copiar body si tiene
                if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                {
                    reqMsg.Content = new StreamContent(context.Request.Body);
                    if (context.Request.ContentType is { } ct2)
                        reqMsg.Content.Headers.TryAddWithoutValidation("Content-Type", ct2);
                }

                try
                {
                    using var response = await client.SendAsync(
                        reqMsg,
                        HttpCompletionOption.ResponseHeadersRead,
                        context.RequestAborted);

                    context.Response.StatusCode = (int)response.StatusCode;

                    foreach (var header in response.Headers)
                    {
                        if (HopByHopHeaders.Contains(header.Key)) continue;
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                    foreach (var header in response.Content.Headers)
                        context.Response.Headers[header.Key] = header.Value.ToArray();

                    await response.Content.CopyToAsync(context.Response.Body);
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 502; // Bad Gateway
                    await context.Response.WriteAsync($"Proxy error: {ex.Message}");
                }
            });
        }

        public async Task StopAsync()
        {
            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
            _httpClient?.Dispose();
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        // ── Helpers ────────────────────────────────────────────────────────────
        private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Connection", "Keep-Alive", "Transfer-Encoding", "TE",
            "Trailer", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate"
        };
    }
}
