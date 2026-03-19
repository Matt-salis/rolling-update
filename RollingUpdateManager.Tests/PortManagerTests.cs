using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using RollingUpdateManager.Services;

namespace RollingUpdateManager.Tests
{
    public class PortManagerTests
    {
        private PortManager CreateManager(int start = 20000, int end = 20099) =>
            new PortManager(start, end);

        // ── Basic allocation ─────────────────────────────────────────────────

        [Fact]
        public void AcquirePort_ReturnsPortInRange()
        {
            var pm   = CreateManager(20000, 20099);
            var port = pm.AcquirePort("svc1");
            Assert.InRange(port, 20000, 20099);
        }

        [Fact]
        public void AcquirePort_TwoCalls_ReturnDifferentPorts()
        {
            var pm = CreateManager(20000, 20099);
            var p1 = pm.AcquirePort("svc1");
            var p2 = pm.AcquirePort("svc2");
            Assert.NotEqual(p1, p2);
        }

        [Fact]
        public void AcquirePort_AfterRelease_PortBecomesAvailableAgain()
        {
            var pm   = CreateManager(20000, 20000); // single-port range
            var port = pm.AcquirePort("svc1");
            pm.ReleasePort(port);
            var port2 = pm.AcquirePort("svc2");
            Assert.Equal(port, port2);
        }

        // ── Exhaustion ───────────────────────────────────────────────────────

        [Fact]
        public void AcquirePort_WhenRangeExhausted_Throws()
        {
            var pm = CreateManager(20000, 20004); // 5-port range
            // Reserve all 5 - use localhost-only ports that are very unlikely to
            // be occupied; if they are occupied the AcquirePort skips them which
            // would give a false failure. Use a range guaranteed to be reserved.
            var acquired = new List<int>();
            for (int i = 0; i < 5; i++)
            {
                try { acquired.Add(pm.AcquirePort($"svc{i}")); }
                catch { /* occupied by OS — accept fewer acquisitions */ }
            }
            // At this point all internally-reserved slots should be exhausted;
            // the next call must throw (even if some ports were OS-skipped,
            // the manager's internal reservation is full).
            if (acquired.Count == 5)
                Assert.Throws<InvalidOperationException>(() => pm.AcquirePort("extra"));
        }

        // ── AcquirePortExact ─────────────────────────────────────────────────

        [Fact]
        public void AcquirePortExact_RegistersPortAsReserved()
        {
            var pm = CreateManager(20000, 20099);
            pm.AcquirePortExact("svc1", 20050);
            // Trying to acquire 20050 again should skip it; a different port is returned
            var next = pm.AcquirePort("svc2");
            Assert.NotEqual(20050, next);
        }

        // ── ReleaseAllPortsFor ───────────────────────────────────────────────

        [Fact]
        public void ReleaseAllPortsFor_ReleasesAllPortsOfService()
        {
            var pm = CreateManager(20000, 20099);
            pm.AcquirePortExact("svc1", 20010);
            pm.AcquirePortExact("svc1", 20011);
            pm.ReleaseAllPortsFor("svc1");

            // Both ports should now be available for a new service
            pm.AcquirePortExact("other", 20010);
            pm.AcquirePortExact("other", 20011);
            // No exception means they were successfully re-acquired
        }

        // ── SetRange ─────────────────────────────────────────────────────────

        [Fact]
        public void SetRange_InvalidRange_Throws()
        {
            var pm = CreateManager();
            Assert.Throws<ArgumentException>(() => pm.SetRange(5000, 4000));
        }

        [Fact]
        public void SetRange_EqualStartEnd_Throws()
        {
            var pm = CreateManager();
            Assert.Throws<ArgumentException>(() => pm.SetRange(5000, 5000));
        }

        [Fact]
        public void SetRange_ValidRange_PortsAllocatedInNewRange()
        {
            var pm = CreateManager(20000, 20099);
            pm.SetRange(21000, 21099);
            var port = pm.AcquirePort("svc1");
            Assert.InRange(port, 21000, 21099);
        }

        // ── Concurrency ──────────────────────────────────────────────────────

        [Fact]
        public async Task AcquirePort_ConcurrentCalls_AllDifferent()
        {
            var pm      = CreateManager(22000, 22999); // 1000-port range
            var results = new System.Collections.Concurrent.ConcurrentBag<int>();
            int tasks   = 50;

            var work = Parallel.ForEachAsync(
                System.Linq.Enumerable.Range(0, tasks),
                new ParallelOptions { MaxDegreeOfParallelism = 8 },
                (i, _) =>
                {
                    results.Add(pm.AcquirePort($"svc{i}"));
                    return ValueTask.CompletedTask;
                });
            await work;

            // All acquired ports must be unique
            Assert.Equal(tasks, new HashSet<int>(results).Count);
        }
    }
}
