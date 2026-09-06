using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.Zattoo.Plugin.LiveTv
{
    /// <summary>Keeps the last FFmpeg standard error lines, bounded in count and in age.</summary>
    /// <remarks>
    /// The count bound keeps the buffer small, because these lines carry signed
    /// URLs even after sanitisation of the known patterns. The age bound matters
    /// just as much: FFmpeg recovers from transient warnings by itself, and a
    /// warning it already survived must not be presented later as the cause of an
    /// unrelated failure.
    /// </remarks>
    public sealed class ZattooStandardErrorBuffer
    {
        private readonly object sync = new object();
        private readonly Queue<Entry> entries = new Queue<Entry>();
        private readonly int capacity;

        public ZattooStandardErrorBuffer()
            : this(12)
        {
        }

        public ZattooStandardErrorBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            this.capacity = capacity;
        }

        public void Add(string line, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (sync)
            {
                entries.Enqueue(new Entry(line, now));
                while (entries.Count > capacity)
                {
                    entries.Dequeue();
                }
            }
        }

        /// <summary>
        /// Reads the lines written within the given window, oldest first. An empty
        /// result is a meaningful answer: FFmpeg said nothing recent, so nothing
        /// should be blamed.
        /// </summary>
        public string[] ReadRecent(DateTimeOffset now, TimeSpan window)
        {
            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(window));
            }

            lock (sync)
            {
                return entries
                    .Where(entry => now - entry.When <= window)
                    .Select(entry => entry.Line)
                    .ToArray();
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                entries.Clear();
            }
        }

        private readonly struct Entry
        {
            public Entry(string line, DateTimeOffset when)
            {
                Line = line;
                When = when;
            }

            public string Line { get; }

            public DateTimeOffset When { get; }
        }
    }
}
