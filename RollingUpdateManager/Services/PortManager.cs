using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RollingUpdateManager.Models;

namespace RollingUpdateManager.Services
{
    /// <summary>
    /// Gestiona la asignación, reserva y liberación de puertos internos.
    /// Thread-safe mediante ConcurrentDictionary y lock puntual.
    /// Usa cursor round-robin para evitar re-escanear desde el inicio en cada llamada,
    /// lo que agotaría el rango cuando hay puertos en proceso de liberación asíncrona.
    /// </summary>
    public class PortManager
    {
        // puerto → serviceId que lo usa
        private readonly ConcurrentDictionary<int, string> _reservedPorts = new();
        private readonly object _scanLock = new();

        private int _rangeStart;
        private int _rangeEnd;
        // Cursor round-robin: la próxima búsqueda empieza aquí en lugar de _rangeStart.
        // Evita que ciclos sucesivos de Start/Stop choquen con las mismas reservas obsoletas.
        private int _nextCandidate;

        public PortManager(int rangeStart = 10000, int rangeEnd = 19999)
        {
            _rangeStart      = rangeStart;
            _rangeEnd        = rangeEnd;
            _nextCandidate   = rangeStart;
        }

        // ── Configurar rango ───────────────────────────────────────────────────
        public void SetRange(int start, int end)
        {
            if (start >= end) throw new ArgumentException("El inicio del rango debe ser menor que el final.");
            lock (_scanLock)
            {
                _rangeStart    = start;
                _rangeEnd      = end;
                _nextCandidate = start;
            }
        }

        // ── Obtener puerto libre ───────────────────────────────────────────────
        /// <summary>
        /// Busca el primer puerto libre en el rango configurado que:
        /// 1) No esté reservado internamente.
        /// 2) No esté en uso por el SO (via IPGlobalProperties, sin abrir sockets).
        /// Usa un cursor round-robin para no re-escanear desde el inicio en cada llamada.
        /// </summary>
        public int AcquirePort(string serviceId)
        {
            // Obtener snapshot de puertos TCP activos del SO una sola vez (fuera del lock).
            // Esto evita llamar a TcpListener.Start() que abre+cierra un socket por cada
            // puerto candidato — muy lento cuando hay >10 intentos fallidos consecutivos.
            var activePorts = GetActiveOsPorts();

            int rangeSize = _rangeEnd - _rangeStart + 1;

            lock (_scanLock)
            {
                for (int i = 0; i < rangeSize; i++)
                {
                    // Wrap-around: si llegamos al final del rango, volvemos al inicio
                    int port = _rangeStart + ((_nextCandidate - _rangeStart + i) % rangeSize);

                    if (_reservedPorts.ContainsKey(port)) continue;
                    if (activePorts.Contains(port))       continue;

                    // Reservar y avanzar cursor para la próxima llamada
                    _reservedPorts[port] = serviceId;
                    _nextCandidate = _rangeStart + ((port - _rangeStart + 1) % rangeSize);
                    return port;
                }
            }

            throw new InvalidOperationException(
                $"No hay puertos libres en el rango {_rangeStart}-{_rangeEnd}. " +
                $"Reservados: {_reservedPorts.Count}. " +
                $"Considera ampliar el rango o reiniciar los servicios en error.");
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

        /// <summary>
        /// Reserva un puerto específico para un servicio (usado en el handoff para
        /// re-registrar los puertos que ya estaban en uso por los procesos adoptados).
        /// No comprueba si está disponible en el SO porque el proceso ya lo tiene abierto.
        /// </summary>
        public void AcquirePortExact(string serviceId, int port)
        {
            _reservedPorts[port] = serviceId;
        }

        // ── Consultas ──────────────────────────────────────────────────────────
        public IReadOnlyDictionary<int, string> ReservedPorts => _reservedPorts;

        public bool IsReserved(int port) => _reservedPorts.ContainsKey(port);

        // ── Verificación de SO ─────────────────────────────────────────────────
        /// <summary>
        /// Devuelve el conjunto de puertos TCP actualmente en uso por el SO.
        /// Usa IPGlobalProperties, que no abre sockets y es mucho más rápido que
        /// TcpListener.Start() cuando se consultan múltiples puertos seguidos.
        /// </summary>
        private static HashSet<int> GetActiveOsPorts()
        {
            var set = new HashSet<int>();
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                foreach (var ep in props.GetActiveTcpListeners())
                    set.Add(ep.Port);
                foreach (var ep in props.GetActiveTcpConnections())
                    set.Add(ep.LocalEndPoint.Port);
            }
            catch { /* Si falla, devolvemos set vacío y dejamos que el proceso falle al bind */ }
            return set;
        }

        /// <summary>
        /// Comprueba que el SO no esté usando el puerto intentando un bind temporal.
        /// Usado externamente para verificaciones ad-hoc; no usar en el hot-path de AcquirePort.
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
