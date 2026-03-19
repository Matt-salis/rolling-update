using System;
using System.Text;

namespace RollingUpdateManager.Models
{
    /// <summary>
    /// Buffer circular de capacidad fija para líneas de log.
    /// Cuando está lleno, sobrescribe la entrada más antigua (no aloca).
    /// NOT thread-safe — los callers deben sincronizar externamente.
    /// </summary>
    public sealed class LogRingBuffer
    {
        private readonly string[] _ring;
        private int _next;   // índice de la próxima escritura
        private int _count;  // entradas válidas (hasta Capacity)

        public int Capacity => _ring.Length;
        public int Count    => _count;

        public LogRingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _ring = new string[capacity];
        }

        /// <summary>Agrega una línea. Si el buffer está lleno, sobreescribe la más antigua.</summary>
        public void Push(string line)
        {
            _ring[_next] = line;
            _next        = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }

        /// <summary>
        /// Reconstruye las últimas <paramref name="maxLines"/> líneas concatenadas con '\n'.
        /// O(k) donde k = min(maxLines, Count) — nunca serializa el buffer completo.
        /// </summary>
        public string BuildText(int maxLines)
        {
            int take = Math.Min(maxLines, _count);
            if (take == 0) return string.Empty;

            int cap   = _ring.Length;
            int start = (_next - take + cap) % cap;
            var sb    = new StringBuilder(take * 80);
            for (int i = 0; i < take; i++)
            {
                var s = _ring[(start + i) % cap];
                if (s is not null) { sb.Append(s); sb.Append('\n'); }
            }
            return sb.ToString();
        }

        /// <summary>Vacía el buffer sin reasignar el array.</summary>
        public void Clear()
        {
            Array.Clear(_ring, 0, _ring.Length);
            _next  = 0;
            _count = 0;
        }

        /// <summary>Copia las entradas válidas en orden cronológico (más antigua primero).</summary>
        public string[] ToArray()
        {
            if (_count == 0) return Array.Empty<string>();
            int cap   = _ring.Length;
            int start = (_next - _count + cap) % cap;
            var arr   = new string[_count];
            for (int i = 0; i < _count; i++)
                arr[i] = _ring[(start + i) % cap];
            return arr;
        }
    }
}
