using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RollingUpdateManager.Models;

namespace RollingUpdateManager.Infrastructure
{
    /// <summary>
    /// Lee y escribe el archivo handoff.json que permite al nuevo exe adoptar
    /// los procesos Java que el exe viejo dejó corriendo durante una actualización.
    ///
    /// Ciclo de vida:
    ///   1. Exe viejo llama <see cref="WriteAsync"/> justo antes de cerrarse.
    ///   2. Exe nuevo llama <see cref="TryReadAndDelete"/> al arrancar.
    ///      - Si existe → ReattachFromHandoffAsync (adoptar PIDs).
    ///      - Si no existe → arranque normal (AutoStart).
    ///   3. El archivo se borra tras la lectura para evitar que un siguiente arranque
    ///      normal intente adoptar PIDs ya muertos.
    /// </summary>
    public class HandoffService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters    = { new JsonStringEnumConverter() }
        };

        private readonly string _filePath;

        public HandoffService(string? directory = null)
        {
            var dir = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RollingUpdateManager", "Data");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "handoff.json");
        }

        /// <summary>
        /// Escribe el estado de handoff de forma atómica (temp → rename).
        /// Llamado por el exe saliente justo antes de cerrarse.
        /// </summary>
        public async Task WriteAsync(HandoffState state, CancellationToken ct = default)
        {
            state.WrittenAt = DateTime.UtcNow;
            var tmp = _filePath + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, state, JsonOpts, ct);
            File.Move(tmp, _filePath, overwrite: true);
        }

        /// <summary>
        /// Lee el archivo de handoff y lo elimina de inmediato.
        /// Retorna null si no existe, si está corrupto o si tiene más de 60 segundos
        /// (indica que pertenece a un arranque anterior fallido, no al swap en curso).
        /// </summary>
        public HandoffState? TryReadAndDelete()
        {
            if (!File.Exists(_filePath)) return null;

            HandoffState? state = null;
            try
            {
                var json = File.ReadAllText(_filePath);
                state = JsonSerializer.Deserialize<HandoffState>(json, JsonOpts);
            }
            catch { /* JSON corrupto → tratar como si no existiera */ }
            finally
            {
                try { File.Delete(_filePath); } catch { }
            }

            if (state is null) return null;

            // Descartar si el handoff tiene más de 60 s: significa que el swap falló
            // o que estamos ante un arranque manual posterior, no ante el nuevo exe
            // lanzado por el bat de actualización.
            if ((DateTime.UtcNow - state.WrittenAt).TotalSeconds > 60)
                return null;

            return state;
        }
    }
}
