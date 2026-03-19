using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using RollingUpdateManager.Models;
using RollingUpdateManager.Services;

namespace RollingUpdateManager.Tests
{
    public class PersistenceServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public PersistenceServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"rum_persist_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private PersistenceService MakeService() => new PersistenceService(_tempDir);

        // ── Load when no file ────────────────────────────────────────────────

        [Fact]
        public async Task LoadAsync_NoFile_ReturnsEmptyAppData()
        {
            var svc  = MakeService();
            var data = await svc.LoadAsync();

            Assert.NotNull(data);
            Assert.Empty(data.Services);
        }

        // ── Save + Load round-trip ───────────────────────────────────────────

        [Fact]
        public async Task SaveAndLoad_PreservesServiceConfig()
        {
            var svc    = MakeService();
            var config = new ServiceConfig
            {
                Id         = "test-id",
                Name       = "My Service",
                JarPath    = @"C:\apps\app.jar",
                PublicPort = 8080,
                AutoStart  = true
            };
            var data = new AppData();
            data.Services.Add(config);

            await svc.SaveAsync(data);
            var loaded = await svc.LoadAsync();

            Assert.Single(loaded.Services);
            var s = loaded.Services[0];
            Assert.Equal("test-id",           s.Id);
            Assert.Equal("My Service",         s.Name);
            Assert.Equal(@"C:\apps\app.jar",   s.JarPath);
            Assert.Equal(8080,                 s.PublicPort);
            Assert.True(s.AutoStart);
        }

        [Fact]
        public async Task SaveAndLoad_PortRanges_Preserved()
        {
            var svc  = MakeService();
            var data = new AppData
            {
                PortRanges = new PortRangeConfig { RangeStart = 15000, RangeEnd = 15999 }
            };

            await svc.SaveAsync(data);
            var loaded = await svc.LoadAsync();

            Assert.Equal(15000, loaded.PortRanges.RangeStart);
            Assert.Equal(15999, loaded.PortRanges.RangeEnd);
        }

        // ── Upsert ───────────────────────────────────────────────────────────

        [Fact]
        public async Task UpsertServiceAsync_NewService_AddsIt()
        {
            var svc    = MakeService();
            var config = new ServiceConfig { Id = "new1", Name = "New" };

            await svc.UpsertServiceAsync(config);
            var data = await svc.LoadAsync();

            Assert.Single(data.Services);
            Assert.Equal("new1", data.Services[0].Id);
        }

        [Fact]
        public async Task UpsertServiceAsync_ExistingService_UpdatesIt()
        {
            var svc = MakeService();
            var initial = new ServiceConfig { Id = "svc1", Name = "Old Name", PublicPort = 8000 };
            await svc.UpsertServiceAsync(initial);

            var updated = new ServiceConfig { Id = "svc1", Name = "New Name", PublicPort = 9000 };
            await svc.UpsertServiceAsync(updated);

            var data = await svc.LoadAsync();
            Assert.Single(data.Services);
            Assert.Equal("New Name", data.Services[0].Name);
            Assert.Equal(9000,       data.Services[0].PublicPort);
        }

        [Fact]
        public async Task UpsertServiceAsync_Multiple_AllPersisted()
        {
            var svc = MakeService();
            for (int i = 0; i < 5; i++)
                await svc.UpsertServiceAsync(new ServiceConfig { Id = $"svc{i}", Name = $"Service {i}" });

            var data = await svc.LoadAsync();
            Assert.Equal(5, data.Services.Count);
        }

        // ── Remove ───────────────────────────────────────────────────────────

        [Fact]
        public async Task RemoveServiceAsync_RemovesCorrectService()
        {
            var svc = MakeService();
            await svc.UpsertServiceAsync(new ServiceConfig { Id = "keep",   Name = "Keep" });
            await svc.UpsertServiceAsync(new ServiceConfig { Id = "delete", Name = "Delete" });

            await svc.RemoveServiceAsync("delete");

            var data = await svc.LoadAsync();
            Assert.Single(data.Services);
            Assert.Equal("keep", data.Services[0].Id);
        }

        [Fact]
        public async Task RemoveServiceAsync_NonExistent_NoError()
        {
            var svc = MakeService();
            await svc.UpsertServiceAsync(new ServiceConfig { Id = "svc1" });

            var ex = await Record.ExceptionAsync(() => svc.RemoveServiceAsync("does-not-exist"));
            Assert.Null(ex);

            // Original service still there
            var data = await svc.LoadAsync();
            Assert.Single(data.Services);
        }

        // ── Atomic write (temp → rename) ─────────────────────────────────────

        [Fact]
        public async Task SaveAsync_NoTempFileLeft_AfterSuccess()
        {
            var svc = MakeService();
            await svc.SaveAsync(new AppData());

            var tmpFile = svc.DataFilePath + ".tmp";
            Assert.False(File.Exists(tmpFile));
        }

        [Fact]
        public async Task SaveAsync_DataFileExists_AfterSave()
        {
            var svc = MakeService();
            await svc.SaveAsync(new AppData());
            Assert.True(File.Exists(svc.DataFilePath));
        }

        // ── Corrupt JSON recovery ─────────────────────────────────────────────

        [Fact]
        public async Task LoadAsync_CorruptJson_ReturnsEmptyAndCreatesBackup()
        {
            var svc = MakeService();
            // Write garbage to the data file
            File.WriteAllText(svc.DataFilePath, "{ NOT VALID JSON ]");

            var data = await svc.LoadAsync();

            Assert.NotNull(data);
            Assert.Empty(data.Services);

            // A backup should have been created
            var backups = Directory.GetFiles(_tempDir, "*.bak.*");
            Assert.NotEmpty(backups);
        }

        // ── Concurrent writes don't corrupt data ─────────────────────────────

        [Fact]
        public async Task UpsertAsync_ConcurrentCalls_NoDuplicates()
        {
            var svc = MakeService();
            const int n = 20;

            await Parallel.ForEachAsync(
                Enumerable.Range(0, n),
                new ParallelOptions { MaxDegreeOfParallelism = 8 },
                async (i, _) =>
                    await svc.UpsertServiceAsync(new ServiceConfig { Id = $"svc{i:D3}", Name = $"S{i}" }));

            var data = await svc.LoadAsync();
            // All n services should be present with unique IDs
            Assert.Equal(n, data.Services.Select(s => s.Id).Distinct().Count());
        }
    }
}
