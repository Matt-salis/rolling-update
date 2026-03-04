using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RollingUpdateManager.Models;

namespace RollingUpdateManager.Services
{
    /// <summary>
    /// Persiste la configuración de todos los servicios en un archivo JSON local.
    /// Thread-safe: usa SemaphoreSlim para evitar escrituras concurrentes.
    /// </summary>
    public class PersistenceService
    {
        // ── Ruta del archivo de datos ──────────────────────────────────────────
        private readonly string _dataFilePath;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ── Constructor ────────────────────────────────────────────────────────
        public PersistenceService(string? dataDirectory = null)
        {
            var dir = dataDirectory
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RollingUpdateManager",
                    "Data");

            Directory.CreateDirectory(dir);
            _dataFilePath = Path.Combine(dir, "services.json");
        }

        // ── Carga ──────────────────────────────────────────────────────────────
        /// <summary>Carga AppData desde disco. Si no existe, retorna vacío.</summary>
        public async Task<AppData> LoadAsync(CancellationToken ct = default)
        {
            if (!File.Exists(_dataFilePath))
                return new AppData();

            try
            {
                await using var stream = File.OpenRead(_dataFilePath);
                var data = await JsonSerializer.DeserializeAsync<AppData>(stream, JsonOptions, ct);
                return data ?? new AppData();
            }
            catch (Exception ex)
            {
                // Si el JSON está corrupto, hacemos backup y empezamos limpio
                var backup = _dataFilePath + $".bak.{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(_dataFilePath, backup, overwrite: true);
                Console.Error.WriteLine($"[Persistence] JSON corrupto, backup en {backup}: {ex.Message}");
                return new AppData();
            }
        }

        // ── Guardado ───────────────────────────────────────────────────────────
        /// <summary>Guarda AppData en disco de forma atómica (write-temp → rename).</summary>
        public async Task SaveAsync(AppData data, CancellationToken ct = default)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                data.LastSaved = DateTime.UtcNow;

                var tmpPath = _dataFilePath + ".tmp";
                await using (var stream = File.Create(tmpPath))
                    await JsonSerializer.SerializeAsync(stream, data, JsonOptions, ct);

                // Reemplazo atómico
                File.Move(tmpPath, _dataFilePath, overwrite: true);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // ── Helper: guarda un solo servicio ────────────────────────────────────
        /// <summary>
        /// El lock cubre todo el ciclo Load→modify→Save para evitar que dos
        /// llamadas concurrentes carguen datos obsoletos y se sobreescriban.
        /// </summary>
        public async Task UpsertServiceAsync(ServiceConfig service, CancellationToken ct = default)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                var data = await LoadLockedAsync(ct);
                var idx  = data.Services.FindIndex(s => s.Id == service.Id);
                if (idx >= 0)
                    data.Services[idx] = service;
                else
                    data.Services.Add(service);
                await SaveLockedAsync(data, ct);
            }
            finally { _writeLock.Release(); }
        }

        // ── Helper: elimina un servicio ─────────────────────────────────────────
        public async Task RemoveServiceAsync(string serviceId, CancellationToken ct = default)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                var data = await LoadLockedAsync(ct);
                data.Services.RemoveAll(s => s.Id == serviceId);
                await SaveLockedAsync(data, ct);
            }
            finally { _writeLock.Release(); }
        }

        // ── Internals sin lock (llamar solo con _writeLock adquirido) ───────────
        private async Task<AppData> LoadLockedAsync(CancellationToken ct)
        {
            if (!File.Exists(_dataFilePath))
                return new AppData();
            try
            {
                await using var stream = File.OpenRead(_dataFilePath);
                var data = await JsonSerializer.DeserializeAsync<AppData>(stream, JsonOptions, ct);
                return data ?? new AppData();
            }
            catch
            {
                return new AppData();
            }
        }

        private async Task SaveLockedAsync(AppData data, CancellationToken ct)
        {
            data.LastSaved = DateTime.UtcNow;
            var tmpPath = _dataFilePath + ".tmp";
            await using (var stream = File.Create(tmpPath))
                await JsonSerializer.SerializeAsync(stream, data, JsonOptions, ct);
            File.Move(tmpPath, _dataFilePath, overwrite: true);
        }

        /// <summary>Ruta completa del archivo de datos.</summary>
        public string DataFilePath => _dataFilePath;
    }
}
