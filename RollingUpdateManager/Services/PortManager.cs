using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using RollingUpdateManager.Models;

namespace RollingUpdateManager.Services
{
    /// <summary>
    /// Gestiona la asignación, reserva y liberación de puertos internos.
    /// Thread-safe mediante ConcurrentDictionary y lock puntual.
    /// </summary>
    public class PortManager
    {
        // puerto → serviceId que lo usa
        private readonly ConcurrentDictionary<int, string> _reservedPorts = new();
        private readonly object _scanLock = new();

        private int _rangeStart;
        private int _rangeEnd;

        public PortManager(int rangeStart = 10000, int rangeEnd = 19999)
        {
            _rangeStart = rangeStart;
            _rangeEnd   = rangeEnd;
        }

        // ── Configurar rango ───────────────────────────────────────────────────
        public void SetRange(int start, int end)
        {
            if (start >= end) throw new ArgumentException("El inicio del rango debe ser menor que el final.");
            _rangeStart = start;
            _rangeEnd   = end;
        }

        // ── Obtener puerto libre ───────────────────────────────────────────────
        /// <summary>
        /// Busca el primer puerto libre en el rango configurado que:
        /// 1) No esté reservado internamente.
        /// 2) No esté en uso por el SO.
        /// </summary>
        /// <param name="serviceId">ID del servicio que reservará el puerto.</param>
        /// <returns>Puerto libre.</returns>
        /// <exception cref="InvalidOperationException">Si no hay puertos disponibles.</exception>
        public int AcquirePort(string serviceId)
        {
            lock (_scanLock)
            {
                for (int port = _rangeStart; port <= _rangeEnd; port++)
                {
                    if (_reservedPorts.ContainsKey(port)) continue;
                    if (!IsPortAvailable(port))            continue;

                    _reservedPorts[port] = serviceId;
                    return port;
                }
                throw new InvalidOperationException(
                    $"No hay puertos libres en el rango {_rangeStart}-{_rangeEnd}.");
            }
        }

        // ── Liberar puerto ─────────────────────────────────────────────────────
        public void ReleasePort(int port) => _reservedPorts.TryRemove(port, out _);

        /// <summary>Libera todos los puertos reservados para un servicio.</summary>
        public void ReleaseAllPortsFor(string serviceId)
        {
            foreach (var kv in _reservedPorts)
                if (kv.Value == serviceId)
                    _reservedPorts.TryRemove(kv.Key, out _);
        }

        // ── Consultas ──────────────────────────────────────────────────────────
        public IReadOnlyDictionary<int, string> ReservedPorts => _reservedPorts;

        public bool IsReserved(int port) => _reservedPorts.ContainsKey(port);

        // ── Verificación de SO ─────────────────────────────────────────────────
        /// <summary>
        /// Comprueba que el SO no esté usando el puerto intentando un bind temporal.
        /// </summary>
        public static bool IsPortAvailable(int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        /// <summary>
        /// Verifica si un puerto acepta conexiones TCP (útil como health-check básico).
        /// </summary>
        public static bool IsPortOpen(string host, int port, int timeoutMs = 500)
        {
            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect(host, port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(timeoutMs);
                if (!success) return false;
                client.EndConnect(result);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
