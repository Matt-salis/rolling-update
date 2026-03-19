using System;
using Xunit;
using RollingUpdateManager.Models;

namespace RollingUpdateManager.Tests
{
    public class LogRingBufferTests
    {
        // ── Construction ────────────────────────────────────────────────────

        [Fact]
        public void Constructor_ZeroCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LogRingBuffer(0));
        }

        [Fact]
        public void Constructor_NegativeCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LogRingBuffer(-1));
        }

        [Fact]
        public void NewBuffer_CountIsZero()
        {
            var buf = new LogRingBuffer(10);
            Assert.Equal(0, buf.Count);
        }

        [Fact]
        public void NewBuffer_BuildText_ReturnsEmpty()
        {
            var buf = new LogRingBuffer(10);
            Assert.Equal(string.Empty, buf.BuildText(100));
        }

        // ── Pushing entries ─────────────────────────────────────────────────

        [Fact]
        public void Push_SingleLine_CountIsOne()
        {
            var buf = new LogRingBuffer(10);
            buf.Push("line1");
            Assert.Equal(1, buf.Count);
        }

        [Fact]
        public void Push_UpToCapacity_CountEqualsCapacity()
        {
            int cap = 5;
            var buf = new LogRingBuffer(cap);
            for (int i = 0; i < cap; i++) buf.Push($"line{i}");
            Assert.Equal(cap, buf.Count);
        }

        [Fact]
        public void Push_BeyondCapacity_CountStaysAtCapacity()
        {
            int cap = 5;
            var buf = new LogRingBuffer(cap);
            for (int i = 0; i < cap + 10; i++) buf.Push($"line{i}");
            Assert.Equal(cap, buf.Count);
        }

        // ── Order & content ─────────────────────────────────────────────────

        [Fact]
        public void BuildText_ReturnsLinesInChronologicalOrder()
        {
            var buf = new LogRingBuffer(10);
            buf.Push("A");
            buf.Push("B");
            buf.Push("C");

            var text = buf.BuildText(10);
            var lines = text.TrimEnd('\n').Split('\n');

            Assert.Equal(new[] { "A", "B", "C" }, lines);
        }

        [Fact]
        public void BuildText_WhenOverflow_OldestLineEvicted()
        {
            var buf = new LogRingBuffer(3);
            buf.Push("old1");
            buf.Push("old2");
            buf.Push("keep1");
            buf.Push("keep2"); // overwrites old1

            var text = buf.BuildText(10);
            Assert.DoesNotContain("old1", text);
            Assert.Contains("old2", text);
            Assert.Contains("keep1", text);
            Assert.Contains("keep2", text);
        }

        [Fact]
        public void BuildText_MaxLinesLimitsOutput()
        {
            var buf = new LogRingBuffer(20);
            for (int i = 0; i < 20; i++) buf.Push($"line{i:D2}");

            var text = buf.BuildText(5);
            var lines = text.TrimEnd('\n').Split('\n');

            Assert.Equal(5, lines.Length);
            // Should be the last 5 lines
            Assert.Equal("line15", lines[0]);
            Assert.Equal("line19", lines[4]);
        }

        [Fact]
        public void BuildText_MaxLinesGreaterThanCount_ReturnsAll()
        {
            var buf = new LogRingBuffer(10);
            buf.Push("X");
            buf.Push("Y");

            var text  = buf.BuildText(100);
            var lines = text.TrimEnd('\n').Split('\n');
            Assert.Equal(2, lines.Length);
        }

        // ── Wrap-around correctness ──────────────────────────────────────────

        [Fact]
        public void BuildText_AfterManyWraps_ReturnsCorrectTail()
        {
            int cap = 4;
            var buf = new LogRingBuffer(cap);
            // Fill 2× capacity to force wrap-around
            for (int i = 0; i < cap * 2; i++) buf.Push($"L{i}");

            var arr = buf.ToArray();
            Assert.Equal(cap, arr.Length);
            // Last 'cap' items should be L4..L7
            for (int i = 0; i < cap; i++)
                Assert.Equal($"L{cap + i}", arr[i]);
        }

        // ── Clear ────────────────────────────────────────────────────────────

        [Fact]
        public void Clear_ResetsCountToZero()
        {
            var buf = new LogRingBuffer(10);
            buf.Push("A");
            buf.Push("B");
            buf.Clear();
            Assert.Equal(0, buf.Count);
        }

        [Fact]
        public void Clear_BuildText_ReturnsEmpty()
        {
            var buf = new LogRingBuffer(10);
            buf.Push("A");
            buf.Clear();
            Assert.Equal(string.Empty, buf.BuildText(10));
        }

        [Fact]
        public void Clear_ThenPush_WorksCorrectly()
        {
            var buf = new LogRingBuffer(3);
            buf.Push("old");
            buf.Clear();
            buf.Push("new");
            var text = buf.BuildText(10).TrimEnd('\n');
            Assert.Equal("new", text);
        }

        // ── ToArray ──────────────────────────────────────────────────────────

        [Fact]
        public void ToArray_Empty_ReturnsEmptyArray()
        {
            var buf = new LogRingBuffer(10);
            Assert.Empty(buf.ToArray());
        }

        [Fact]
        public void ToArray_MatchesBuildTextLines()
        {
            var buf = new LogRingBuffer(10);
            string[] expected = { "alpha", "beta", "gamma" };
            foreach (var s in expected) buf.Push(s);

            Assert.Equal(expected, buf.ToArray());
        }

        // ── Capacity property ────────────────────────────────────────────────

        [Theory]
        [InlineData(1)]
        [InlineData(50)]
        [InlineData(300)]
        public void Capacity_MatchesConstructorArgument(int cap)
        {
            var buf = new LogRingBuffer(cap);
            Assert.Equal(cap, buf.Capacity);
        }
    }
}
