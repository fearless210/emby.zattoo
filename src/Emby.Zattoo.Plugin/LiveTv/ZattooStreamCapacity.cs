using System;
using System.Threading;

namespace Emby.Zattoo.Plugin.LiveTv
{
    internal sealed class ZattooStreamCapacity
    {
        private readonly object syncRoot = new object();
        private int limit = 1;
        private int activeStreams;

        public int Limit
        {
            get
            {
                lock (syncRoot)
                {
                    return limit;
                }
            }
        }

        public int ActiveStreams
        {
            get
            {
                lock (syncRoot)
                {
                    return activeStreams;
                }
            }
        }

        public void UpdateLimit(int value)
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            lock (syncRoot)
            {
                limit = value;
            }
        }

        public IDisposable? TryAcquire()
        {
            lock (syncRoot)
            {
                if (activeStreams >= limit)
                {
                    return null;
                }

                activeStreams++;
                return new Lease(this);
            }
        }

        private void Release()
        {
            lock (syncRoot)
            {
                if (activeStreams > 0)
                {
                    activeStreams--;
                }
            }
        }

        private sealed class Lease : IDisposable
        {
            private ZattooStreamCapacity? owner;

            public Lease(ZattooStreamCapacity owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.Release();
            }
        }
    }
}
