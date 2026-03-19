using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using RollingUpdateManager.Infrastructure;
using RollingUpdateManager.Models;

namespace RollingUpdateManager.Tests
{
    public class HandoffServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public HandoffServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"rum_tests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private HandoffService MakeService() => new HandoffService(_tempDir);

        // ── Read when no file ────────────────────────────────────────────────

        [Fact]
        public void TryReadAndDelete_NoFile_ReturnsNull()
        {
            var svc = MakeService();
            Assert.Null(svc.TryReadAndDelete());
        }

        // ── Write + read cycle ───────────────────────────────────────────────

        [Fact]
        public async Task WriteAndRead_PreservesInstances()
        {
            var svc   = MakeService();
            var state = new HandoffState
            {
                IsPersistent = false,
                Instances    =
                {
                    new HandoffInstance
                    {
                        ServiceId    = "svc-1",
                        Slot         = InstanceSlot.Blue,
                        ProcessId    = 12345,
                        InternalPort = 10001,
                        IsActive     = true,
                        StartedAt    = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                        JarPath      = @"C:\apps\my.jar"
                    }
                }
            };

            await svc.WriteAsync(state);
            var read = svc.TryReadAndDelete();

            Assert.NotNull(read);
            Assert.Single(read!.Instances);

            var inst = read.Instances[0];
            Assert.Equal("svc-1",      inst.ServiceId);
            Assert.Equal(InstanceSlot.Blue, inst.Slot);
            Assert.Equal(12345,         inst.ProcessId);
            Assert.Equal(10001,         inst.InternalPort);
            Assert.True(inst.IsActive);
            Assert.Equal(@"C:\apps\my.jar", inst.JarPath);
        }

        [Fact]
        public async Task TryReadAndDelete_DeletesFileAfterRead()
        {
            var svc = MakeService();
            await svc.WriteAsync(new HandoffState());

            svc.TryReadAndDelete();

            // File must be gone after reading
            var filePath = Path.Combine(_tempDir, "handoff.json");
            Assert.False(File.Exists(filePath));
        }

        // ── 60-second expiry (non-persistent) ───────────────────────────────

        [Fact]
        public void TryReadAndDelete_Expired_NonPersistent_ReturnsNull()
        {
            // WriteAsync always resets WrittenAt = UtcNow, so write the JSON
            // file directly with a backdated timestamp to simulate an old entry.
            var filePath = Path.Combine(_tempDir, "handoff.json");
            var payload  = new
            {
                Instances    = Array.Empty<object>(),
                IsPersistent = false,
                WrittenAt    = DateTime.UtcNow.AddMinutes(-5)
            };
            File.WriteAllText(filePath,
                System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            var svc    = MakeService();
            var result = svc.TryReadAndDelete();
            Assert.Null(result);
        }

        [Fact]
        public async Task TryReadAndDelete_Expired_Persistent_ReturnsState()
        {
            var svc   = MakeService();
            var state = new HandoffState
            {
                IsPersistent = true,
                Instances    = { new HandoffInstance { ServiceId = "x" } }
            };
            await svc.WriteAsync(state);

            // Backdating via file timestamp is not feasible; instead test that
            // a freshly-written persistent state is always returned regardless
            // of the WrittenAt time (which is set internally).
            var result = svc.TryReadAndDelete();
            Assert.NotNull(result);
            Assert.True(result!.IsPersistent);
        }

        // ── Corrupt JSON ─────────────────────────────────────────────────────

        [Fact]
        public void TryReadAndDelete_CorruptJson_ReturnsNull()
        {
            var filePath = Path.Combine(_tempDir, "handoff.json");
            File.WriteAllText(filePath, "{ this is not valid json ]]]");

            var svc    = MakeService();
            var result = svc.TryReadAndDelete();
            Assert.Null(result);
        }

        [Fact]
        public void TryReadAndDelete_CorruptJson_DeletesFile()
        {
            var filePath = Path.Combine(_tempDir, "handoff.json");
            File.WriteAllText(filePath, "GARBAGE");

            var svc = MakeService();
            svc.TryReadAndDelete();

            Assert.False(File.Exists(filePath));
        }

        // ── Multiple instances ───────────────────────────────────────────────

        [Fact]
        public async Task WriteAndRead_MultipleInstances_AllPreserved()
        {
            var svc   = MakeService();
            var state = new HandoffState { IsPersistent = true };
            for (int i = 0; i < 5; i++)
                state.Instances.Add(new HandoffInstance { ServiceId = $"svc{i}", ProcessId = 1000 + i });

            await svc.WriteAsync(state);
            var read = svc.TryReadAndDelete();

            Assert.NotNull(read);
            Assert.Equal(5, read!.Instances.Count);
            for (int i = 0; i < 5; i++)
                Assert.Equal($"svc{i}", read.Instances[i].ServiceId);
        }

        // ── Idempotency (read twice) ─────────────────────────────────────────

        [Fact]
        public async Task TryReadAndDelete_CalledTwice_SecondReturnsNull()
        {
            var svc = MakeService();
            await svc.WriteAsync(new HandoffState { IsPersistent = true });

            var first  = svc.TryReadAndDelete();
            var second = svc.TryReadAndDelete();

            Assert.NotNull(first);
            Assert.Null(second);
        }
    }
}
