using System;
using System.Collections.Generic;

namespace Emby.Zattoo.Plugin.LiveTv
{
    /// <summary>Bounds how often one live stream may be reopened.</summary>
    /// <remarks>
    /// Reopening asks the provider for a new stream, so a stream that fails
    /// immediately every time would hammer the account and hold its concurrent
    /// stream capacity for nothing. A sliding window lets a long recording
    /// survive several unrelated incidents while still refusing a loop.
    /// </remarks>
    public sealed class ZattooStreamRestartBudget
    {
        private readonly object sync = new object();
        private readonly Queue<DateTimeOffset> restarts = new Queue<DateTimeOffset>();
        private readonly int maximumRestarts;
        private readonly TimeSpan window;

        public ZattooStreamRestartBudget()
            : this(3, TimeSpan.FromMinutes(5))
        {
        }

        public ZattooStreamRestartBudget(int maximumRestarts, TimeSpan window)
        {
            if (maximumRestarts < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumRestarts));
            }

            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(window));
            }

            this.maximumRestarts = maximumRestarts;
            this.window = window;
        }

        /// <summary>Gets the restarts already spent inside the current window.</summary>
        public int SpentRestarts
        {
            get
            {
                lock (sync)
                {
                    return restarts.Count;
                }
            }
        }

        /// <summary>
        /// Takes one restart from the budget, or returns false when the window is
        /// already full.
        /// </summary>
        public bool TryAcquire(DateTimeOffset now)
        {
            lock (sync)
            {
                while (restarts.Count > 0 && now - restarts.Peek() >= window)
                {
                    restarts.Dequeue();
                }

                if (restarts.Count >= maximumRestarts)
                {
                    return false;
                }

                restarts.Enqueue(now);
                return true;
            }
        }
    }
}
