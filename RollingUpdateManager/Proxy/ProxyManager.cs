using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RollingUpdateManager.Models;

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
            string       serviceId,
            int          publicPort,
            int          internalPort,
            ProxyMetrics metrics,
            int          headerTimeoutSeconds = 240,
            int          bodyTimeoutSeconds   = 230,
            CancellationToken ct = default)
        {
            if (_entries.TryGetValue(serviceId, out var existing))
            {
                existing.UpdateTarget(internalPort);
                return;
            }

            var entry = new ProxyEntry(serviceId, publicPort, internalPort, metrics,
                                       headerTimeoutSeconds, bodyTimeoutSeconds);
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
        /// Espera (graceful drain) a que las requests en vuelo hacia <paramref name="oldPort"/>
        /// terminen antes de proceder a matar ese proceso.
        /// Timeout de seguridad: si en <paramref name="timeout"/> no drena, continúa igualmente.
        /// </summary>
        public async Task DrainOldTargetAsync(string serviceId, int oldPort, TimeSpan timeout)
        {
            if (_entries.TryGetValue(serviceId, out var entry))
                await entry.DrainOldTargetAsync(oldPort, timeout);
        }

        /// <summary>Requests actualmente en vuelo hacia un puerto interno concreto.</summary>
        public int GetInFlight(string serviceId, int targetPort) =>
            _entries.TryGetValue(serviceId, out var e) ? e.GetInFlight(targetPort) : 0;

        /// <summary>
        /// Obtiene el puerto interno al que apunta actualmente el proxy.
        /// </summary>
        public int? GetCurrentTarget(string serviceId) =>
            _entries.TryGetValue(serviceId, out var e) ? e.CurrentTarget : null;

        /// <summary>
        /// Devuelve el puerto público en el que el proxy está escuchando actualmente,
        /// o null si aún no existe la entrada.
        /// </summary>
        public int? GetCurrentPublicPort(string serviceId) =>
            _entries.TryGetValue(serviceId, out var e) ? e.PublicPort : null;

        /// <summary>
        /// Detiene el listener actual y lo reinicia en un nuevo puerto público.
        /// Necesario cuando el usuario cambia el puerto público mientras el servicio está corriendo.
        /// </summary>
        public async Task RebindPublicPortAsync(
            string       serviceId,
            int          newPublicPort,
            int          currentInternalPort,
            ProxyMetrics metrics,
            int          headerTimeoutSeconds = 240,
            int          bodyTimeoutSeconds   = 230,
            CancellationToken ct = default)
        {
            // Parar listener viejo
            if (_entries.TryRemove(serviceId, out var old))
                await old.StopAsync();

            // Crear nuevo listener en el puerto nuevo
            var entry = new ProxyEntry(serviceId, newPublicPort, currentInternalPort, metrics,
                                       headerTimeoutSeconds, bodyTimeoutSeconds);
            await entry.StartAsync(ct);
            _entries[serviceId] = entry;
        }

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
        private readonly string       _serviceId;
        private readonly int          _publicPort;
        private volatile int          _currentTarget;   // puerto interno actual (volatile para cambio atómico)
        private readonly ProxyMetrics _metrics;
        private readonly int          _headerTimeoutSeconds;
        private readonly int          _bodyTimeoutSeconds;

        private IHost?      _host;
        private HttpClient? _httpClient;  // un único client/handler; reutilizado entre updates

        // ── Graceful handoff: contadores de requests en vuelo por puerto ───────
        // Usamos StrongBox<int> para poder hacer Interlocked.Increment/Decrement
        // sin lambda → cero allocaciones por request en el hot path.
        private readonly ConcurrentDictionary<int, StrongBox<int>> _inFlight = new();

        // ── Cache de HttpMethod para evitar 'new HttpMethod(string)' por request ─
        private static readonly ConcurrentDictionary<string, HttpMethod> _methodCache = new(StringComparer.OrdinalIgnoreCase)
        {
            ["GET"]     = HttpMethod.Get,
            ["POST"]    = HttpMethod.Post,
            ["PUT"]     = HttpMethod.Put,
            ["DELETE"]  = HttpMethod.Delete,
            ["PATCH"]   = HttpMethod.Patch,
            ["HEAD"]    = HttpMethod.Head,
            ["OPTIONS"] = HttpMethod.Options,
        };

        public int CurrentTarget => _currentTarget;
        public int PublicPort    => _publicPort;

        public ProxyEntry(string serviceId, int publicPort, int initialTarget, ProxyMetrics metrics,
                           int headerTimeoutSeconds = 240, int bodyTimeoutSeconds = 230)
        {
            _serviceId            = serviceId;
            _publicPort           = publicPort;
            _currentTarget        = initialTarget;
            _metrics              = metrics;
            _headerTimeoutSeconds = headerTimeoutSeconds > 0 ? headerTimeoutSeconds : 240;
            _bodyTimeoutSeconds   = bodyTimeoutSeconds   > 0 ? bodyTimeoutSeconds   : 230;
        }

        // ── Cambio dinámico de target (thread-safe) ────────────────────────────
        // Nuevas requests se redirigen al newTarget inmediatamente.
        // Las requests en vuelo al target anterior siguen con su conexión hasta terminar.
        // No se crea un nuevo HttpClient (evita acumulación de connection pools).
        public void UpdateTarget(int newTarget) =>
            Interlocked.Exchange(ref _currentTarget, newTarget);

        /// <summary>
        /// Espera a que todas las requests en vuelo dirigidas a <paramref name="oldTarget"/>
        /// terminen, o hasta que expire <paramref name="timeout"/>.
        /// Llama a <see cref="UpdateTarget"/> antes de llamar a este método para que
        /// las nuevas requests ya vayan al slot nuevo mientras esperamos el drain.
        /// </summary>
        public async Task DrainOldTargetAsync(int oldTarget, TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                int active = _inFlight.TryGetValue(oldTarget, out var box) ? box.Value : 0;
                if (active <= 0) return;
                await Task.Delay(50);   // poll cada 50 ms — overhead mínimo
            }
            // Timeout alcanzado: las requests restantes serán cortadas al matar el proceso
        }

        /// <summary>Requests actualmente en vuelo hacia un puerto interno.</summary>
        public int GetInFlight(int targetPort) =>
            _inFlight.TryGetValue(targetPort, out var box) ? box.Value : 0;

        // ── Arrancar listener ──────────────────────────────────────────────
        public async Task StartAsync(CancellationToken ct = default)
        {
            // Verificar disponibilidad del puerto público antes de intentar el bind.
            // Da un mensaje de error claro en lugar de la excepción cruda de Kestrel.
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, _publicPort);
                probe.Start();
                probe.Stop();
            }
            catch (SocketException)
            {
                throw new InvalidOperationException(
                    $"El puerto público {_publicPort} ya está en uso. " +
                    "Detiene el proceso que lo ocupa o elige otro puerto.");
            }
            // Un solo handler con pool de conexiones que dura toda la vida del proxy.
            // Las peticiones construyen la URL con _currentTarget en cada llamada.
            _httpClient = new HttpClient(
                new SocketsHttpHandler
                {
                    // Mantener conexiones loopback vivas 5 min — evita TCP handshake por request
                    PooledConnectionLifetime    = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    // Loopback no necesita DNS; 2s es más que suficiente
                    ConnectTimeout              = TimeSpan.FromSeconds(2),
                    // Máximo de conexiones simultáneas por endpoint (JAR en loopback)
                    // Spring Boot por defecto acepta 200 threads; 50 es razonable para el proxy
                    MaxConnectionsPerServer     = 50,
                    // Deshabilitar proxy del sistema: las peticiones son siempre a localhost
                    UseProxy                    = false,
                    AllowAutoRedirect           = false,    // el JAR maneja sus propios redirects
                    // ResponseDrainTimeout: tiempo máximo para drenar un body de respuesta
                    // (evita que conexiones pesadas bloqueen el pool indefinidamente)
                    ResponseDrainTimeout        = TimeSpan.FromSeconds(_headerTimeoutSeconds),
                })
            {
                // Timeout global por request: tiempo hasta recibir los HEADERS de respuesta.
                // No aplica al tiempo de transferencia del body (eso lo controla bodyCts abajo).
                Timeout = TimeSpan.FromSeconds(_headerTimeoutSeconds)
            };

            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    // Suprimir todo el logging de ASP.NET / Kestrel: con 6 proxies activos
                    // el overhead de ILogger es visible en latencia y memoria.
                    logging.ClearProviders();
                })
                .ConfigureWebHostDefaults(web =>
                {
                    web.UseKestrel(opts =>
                    {
                        opts.Listen(IPAddress.Any, _publicPort);
                        // Suprimir Kestrel request logging
                        opts.AddServerHeader = false;
                    });
                    web.Configure(ConfigurePipeline);
                    web.SuppressStatusMessages(true);
                })
                .Build();

            await _host.StartAsync(ct);
        }

        // ── Pipeline de reenvío ────────────────────────────────────────────────
        private void ConfigurePipeline(IApplicationBuilder app)
        {
            app.Run(async context =>
            {
                // Capturar el target en el momento de aceptar la request.
                var targetPort = _currentTarget;
                var client     = _httpClient!;

                // Incrementar contador sin lambda (Interlocked = cero allocaciones)
                var box = _inFlight.GetOrAdd(targetPort, _ => new StrongBox<int>(0));
                Interlocked.Increment(ref box.Value);

                // Construir URL destino sin UriBuilder (evita allocación del objeto y parseo)
                var req = context.Request;
                var qs  = req.QueryString.HasValue ? req.QueryString.Value : string.Empty;
                var targetUrl = string.Concat("http://localhost:", targetPort.ToString(),
                                              req.Path.Value ?? "/", qs);

                // Obtener HttpMethod desde caché — sin 'new HttpMethod(string)' por request
                var method = _methodCache.GetOrAdd(req.Method, m => new HttpMethod(m));

                using var reqMsg = new HttpRequestMessage
                {
                    Method     = method,
                    RequestUri = new Uri(targetUrl)
                };

                // Copiar headers — StringValues es implícitamente IEnumerable<string?>, sin ToArray()
                foreach (var header in req.Headers)
                {
                    if (HopByHopHeaders.Contains(header.Key)) continue;
                    IEnumerable<string?> vals = header.Value!;
                    if (!reqMsg.Headers.TryAddWithoutValidation(header.Key, vals))
                        reqMsg.Content?.Headers.TryAddWithoutValidation(header.Key, vals);
                }

                // Copiar body cuando proceda
                if (req.ContentLength > 0
                    || req.Headers.ContainsKey("Transfer-Encoding")
                    || (req.ContentLength == null && req.Body.CanRead
                        && req.Method is "POST" or "PUT" or "PATCH"))
                {
                    reqMsg.Content = new StreamContent(req.Body);
                    if (req.ContentType is { } ct2)
                        reqMsg.Content.Headers.TryAddWithoutValidation("Content-Type", ct2);
                }

                var sw = Stopwatch.StartNew();
                bool isError = false;
                try
                {
                    using var response = await client.SendAsync(
                        reqMsg,
                        HttpCompletionOption.ResponseHeadersRead,
                        context.RequestAborted);

                    context.Response.StatusCode = (int)response.StatusCode;
                    isError = (int)response.StatusCode >= 400;

                    foreach (var header in response.Headers)
                    {
                        if (HopByHopHeaders.Contains(header.Key)) continue;
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                    foreach (var header in response.Content.Headers)
                        context.Response.Headers[header.Key] = header.Value.ToArray();

                    // CTS propio (no linked): evita la allocación de LinkedCancellationTokenSource
                    // en cada request. Si el cliente cancela, CopyToAsync lo detecta porque
                    // context.Response.Body está vinculado al socket.
                    // bodyTimeout < headerTimeout: el timeout de body dispara primero,
                    // permitiendo devolver un 504 limpio antes de que el HttpClient corte el TCP.
                    using var bodyCts = new CancellationTokenSource(TimeSpan.FromSeconds(_bodyTimeoutSeconds));
                    await response.Content.CopyToAsync(context.Response.Body, bodyCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (context.RequestAborted.IsCancellationRequested)
                    {
                        // El cliente upstream cortó la conexión voluntariamente (navegador cerrado,
                        // timeout del cliente, etc.). No es un error del proxy ni del backend.
                        isError = false;
                    }
                    else
                    {
                        // bodyCts expiró: el backend tardó demasiado en transferir el body.
                        isError = true;
                        if (!context.Response.HasStarted)
                        {
                            context.Response.StatusCode = 504;
                            await context.Response.WriteAsync("Gateway Timeout: el servicio no terminó de enviar la respuesta a tiempo.");
                        }
                    }
                }
                // catch (TaskCanceledException) eliminado: OperationCanceledException lo cubre completamente
                catch (Exception ex)
                {
                    isError = true;
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = 502;
                        await context.Response.WriteAsync($"Proxy error: {ex.Message}");
                    }
                }
                finally
                {
                    sw.Stop();
                    _metrics.RecordRequest(sw.ElapsedMilliseconds, isError);

                    // Decrementar sin lambda
                    Interlocked.Decrement(ref box.Value);
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
