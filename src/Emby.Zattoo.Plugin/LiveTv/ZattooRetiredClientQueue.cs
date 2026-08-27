using System;
using System.Collections.Generic;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Plugin.LiveTv
{
    /// <summary>
    /// Holds clients replaced by a configuration change until the streams that
    /// were opening with them cannot reference them any more. A live stream only
    /// uses its client while opening, so a bounded grace period is enough.
    /// </summary>
    internal sealed class ZattooRetiredClientQueue
    {
        private readonly object syncRoot = new object();
        private readonly List<RetiredClient> retiredClients =
            new List<RetiredClient>();
        private readonly TimeSpan gracePeriod;

        public ZattooRetiredClientQueue(TimeSpan gracePeriod)
        {
            if (gracePeriod < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(gracePeriod));
            }

            this.gracePeriod = gracePeriod;
        }

        public int Count
        {
            get
            {
                lock (syncRoot)
                {
                    return retiredClients.Count;
                }
            }
        }

        public void Retire(IZattooClient client, DateTimeOffset now)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            lock (syncRoot)
            {
                retiredClients.Add(new RetiredClient(client, now.Add(gracePeriod)));
            }
        }

        /// <summary>
        /// Removes and returns the clients whose grace period elapsed. The caller
        /// disposes them outside of any lock, because disposing a client drains
        /// its guide enrichment worker.
        /// </summary>
        public IReadOnlyList<IZattooClient> TakeExpired(DateTimeOffset now)
        {
            lock (syncRoot)
            {
                if (retiredClients.Count == 0)
                {
                    return Array.Empty<IZattooClient>();
                }

                var expired = new List<IZattooClient>();
                for (var index = retiredClients.Count - 1; index >= 0; index--)
                {
                    if (retiredClients[index].DisposableAt > now)
                    {
                        continue;
                    }

                    expired.Add(retiredClients[index].Client);
                    retiredClients.RemoveAt(index);
                }

                return expired;
            }
        }

        /// <summary>Removes and returns every remaining client.</summary>
        public IReadOnlyList<IZattooClient> TakeAll()
        {
            lock (syncRoot)
            {
                if (retiredClients.Count == 0)
                {
                    return Array.Empty<IZattooClient>();
                }

                var all = new List<IZattooClient>(retiredClients.Count);
                foreach (var retired in retiredClients)
                {
                    all.Add(retired.Client);
                }

                retiredClients.Clear();
                return all;
            }
        }

        private sealed class RetiredClient
        {
            public RetiredClient(IZattooClient client, DateTimeOffset disposableAt)
            {
                Client = client;
                DisposableAt = disposableAt;
            }

            public IZattooClient Client { get; }

            public DateTimeOffset DisposableAt { get; }
        }
    }
}
