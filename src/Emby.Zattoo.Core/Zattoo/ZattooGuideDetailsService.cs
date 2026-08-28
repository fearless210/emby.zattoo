using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Zattoo
{
    internal sealed class ZattooGuideDetailsService : IDisposable
    {
        private const int BatchSize = 20;
        private const long ProgressInterval = 5000;
        private const int PriorityStreamOpen = 0;
        private const int PriorityCurrent = 0;
        private const int PriorityNext = 1;
        private const int PriorityFavorite = 2;
        private const int PriorityNearTerm = 3;
        private const int PriorityBackground = 4;
        private static readonly TimeSpan DefaultDetailRetention = TimeSpan.FromHours(6);
        private static readonly TimeSpan DefaultCleanupInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan NearTermHorizon = TimeSpan.FromHours(24);
        private static readonly TimeSpan NextProgramHorizon = TimeSpan.FromHours(8);
        private static readonly TimeSpan IncompleteDetailRetry = TimeSpan.FromHours(6);
        private static readonly TimeSpan DetailLeadTime = TimeSpan.FromDays(2);

        private readonly object syncRoot = new object();
        private readonly SortedSet<PendingProgram> pendingPrograms =
            new SortedSet<PendingProgram>(PendingProgramComparer.Instance);
        private readonly Dictionary<string, PendingProgram> pendingById =
            new Dictionary<string, PendingProgram>(StringComparer.Ordinal);
        private readonly HashSet<string> inFlightIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DetailCacheEntry> detailsById =
            new Dictionary<string, DetailCacheEntry>(StringComparer.Ordinal);
        private readonly Func<
            IReadOnlyList<string>,
            CancellationToken,
            Task<IReadOnlyList<ZattooProgramDetails>>> fetchDetails;
        private readonly TimeSpan requestInterval;
        private readonly TimeSpan retryDelay;
        private readonly TimeSpan detailRetention;
        private readonly TimeSpan cleanupInterval;
        private readonly Func<DateTimeOffset> utcNow;
        private readonly Action<ZattooGuideDetailsProgress>? reportProgress;
        private readonly ZattooGuideDetailsCacheStore? persistentStore;
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0, 1);
        private readonly CancellationTokenSource cancellation =
            new CancellationTokenSource();
        private readonly Task worker;
        private DateTimeOffset nextCleanupAt = DateTimeOffset.MinValue;
        private long processedPrograms;
        private long failedBatches;
        private long removedPrograms;
        private long nextProgressAt = ProgressInterval;
        private bool cycleActive;
        private bool stopped;
        private bool disposed;

        public ZattooGuideDetailsService(
            TimeSpan requestInterval,
            TimeSpan retryDelay,
            Func<
                IReadOnlyList<string>,
                CancellationToken,
                Task<IReadOnlyList<ZattooProgramDetails>>> fetchDetails,
            Action<ZattooGuideDetailsProgress>? reportProgress,
            string cachePath = "",
            string cacheScope = "")
            : this(
                requestInterval,
                retryDelay,
                fetchDetails,
                reportProgress,
                DefaultDetailRetention,
                DefaultCleanupInterval,
                () => DateTimeOffset.UtcNow,
                cachePath,
                cacheScope)
        {
        }

        internal ZattooGuideDetailsService(
            TimeSpan requestInterval,
            TimeSpan retryDelay,
            Func<
                IReadOnlyList<string>,
                CancellationToken,
                Task<IReadOnlyList<ZattooProgramDetails>>> fetchDetails,
            Action<ZattooGuideDetailsProgress>? reportProgress,
            TimeSpan detailRetention,
            TimeSpan cleanupInterval,
            Func<DateTimeOffset> utcNow,
            string cachePath = "",
            string cacheScope = "")
        {
            if (requestInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(requestInterval));
            }

            if (retryDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retryDelay));
            }

            if (detailRetention < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(detailRetention));
            }

            if (cleanupInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(cleanupInterval));
            }

            this.requestInterval = requestInterval;
            this.retryDelay = retryDelay;
            this.detailRetention = detailRetention;
            this.cleanupInterval = cleanupInterval;
            this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            this.fetchDetails = fetchDetails
                ?? throw new ArgumentNullException(nameof(fetchDetails));
            this.reportProgress = reportProgress;
            persistentStore = CreatePersistentStore(cachePath, cacheScope);
            LoadPersistentCache();
            worker = Task.Run(RunAsync);
        }

        public void QueuePrograms(
            IEnumerable<ZattooProgram> programs,
            IReadOnlyCollection<string> favoriteChannelIds,
            DateTimeOffset requestedStart,
            DateTimeOffset requestedEnd,
            int? forcedPriority = null)
        {
            if (programs == null)
            {
                throw new ArgumentNullException(nameof(programs));
            }

            if (favoriteChannelIds == null)
            {
                throw new ArgumentNullException(nameof(favoriteChannelIds));
            }

            var programArray = programs.ToArray();
            var favorites = new HashSet<string>(
                favoriteChannelIds,
                StringComparer.Ordinal);
            var now = utcNow();
            var nextProgramIds = FindNextProgramIds(programArray, now);
            var added = false;
            ZattooGuideDetailsProgress? progress = null;
            lock (syncRoot)
            {
                if (stopped)
                {
                    return;
                }

                CleanupIfDueLocked(now);
                foreach (var program in programArray)
                {
                    if (string.IsNullOrWhiteSpace(program.Id)
                        || program.EndDate <= now
                        || program.EndDate <= requestedStart
                        || program.StartDate >= requestedEnd
                        || !NeedsDetails(program))
                    {
                        continue;
                    }

                    var priority = forcedPriority ?? CalculatePriority(
                        program,
                        favorites,
                        nextProgramIds,
                        now);
                    if (priority == PriorityBackground)
                    {
                        continue;
                    }

                    var expiresAt = program.EndDate.Add(detailRetention);
                    var fingerprint = CreateProgramFingerprint(program);
                    if (detailsById.TryGetValue(program.Id, out var cached))
                    {
                        if (cached.ExpiresAt <= now)
                        {
                            detailsById.Remove(program.Id);
                            removedPrograms++;
                        }
                        else if (string.Equals(
                                cached.Fingerprint,
                                fingerprint,
                                StringComparison.Ordinal)
                            && cached.RefreshAfter > now
                            && (!forcedPriority.HasValue
                                || HasCompleteDescription(cached.Details)))
                        {
                            if (expiresAt > cached.ExpiresAt)
                            {
                                cached.ExpiresAt = expiresAt;
                            }

                            continue;
                        }
                        else
                        {
                            detailsById.Remove(program.Id);
                        }
                    }

                    var pending = new PendingProgram(
                        program.Id,
                        program.ChannelId,
                        fingerprint,
                        priority,
                        program.StartDate,
                        expiresAt);
                    if (inFlightIds.Contains(program.Id))
                    {
                        continue;
                    }

                    if (pendingById.TryGetValue(program.Id, out var existing))
                    {
                        if (!string.Equals(
                                existing.Fingerprint,
                                pending.Fingerprint,
                                StringComparison.Ordinal))
                        {
                            pendingPrograms.Remove(existing);
                            pendingPrograms.Add(pending);
                            pendingById[program.Id] = pending;
                        }
                        else
                        {
                            var merged = existing.Merge(pending);
                            if (!PendingProgramComparer.Instance.Equals(existing, merged))
                            {
                                pendingPrograms.Remove(existing);
                                pendingPrograms.Add(merged);
                                pendingById[program.Id] = merged;
                            }
                        }

                        continue;
                    }

                    pendingPrograms.Add(pending);
                    pendingById[program.Id] = pending;
                    added = true;
                }

                if (added && !cycleActive)
                {
                    cycleActive = true;
                    progress = CreateProgressLocked(
                        ZattooGuideDetailsProgressKind.Started);
                }

                if (added && signal.CurrentCount == 0)
                {
                    signal.Release();
                }
            }

            Report(progress);
        }

        public void RestrictToChannels(IReadOnlyCollection<string> channelIds)
        {
            if (channelIds == null)
            {
                throw new ArgumentNullException(nameof(channelIds));
            }

            var allowed = new HashSet<string>(channelIds, StringComparer.Ordinal);
            lock (syncRoot)
            {
                var excluded = pendingPrograms
                    .Where(program => !allowed.Contains(program.ChannelId))
                    .ToArray();
                foreach (var program in excluded)
                {
                    pendingPrograms.Remove(program);
                    pendingById.Remove(program.Id);
                }

                removedPrograms += excluded.Length;
            }
        }

        public void PrioritizePrograms(IEnumerable<ZattooProgram> programs)
        {
            if (programs == null)
            {
                throw new ArgumentNullException(nameof(programs));
            }

            var now = utcNow();
            var candidates = programs
                .Where(program => program.EndDate > now)
                .GroupBy(program => program.ChannelId, StringComparer.Ordinal)
                .SelectMany(group =>
                {
                    var current = group
                        .Where(program => program.StartDate <= now)
                        .OrderBy(program => program.StartDate)
                        .Take(1);
                    var next = group
                        .Where(program => program.StartDate > now)
                        .OrderBy(program => program.StartDate)
                        .Take(1);
                    return current.Concat(next);
                })
                .GroupBy(program => program.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (candidates.Length == 0)
            {
                return;
            }

            QueuePrograms(
                candidates,
                Array.Empty<string>(),
                now.AddHours(-1),
                now.Add(NextProgramHorizon),
                PriorityStreamOpen);
        }

        public void ApplyDetails(ZattooProgram program)
        {
            if (program == null)
            {
                throw new ArgumentNullException(nameof(program));
            }

            ZattooProgramDetails? details;
            lock (syncRoot)
            {
                if (!detailsById.TryGetValue(program.Id, out var cached)
                    || cached.ExpiresAt <= utcNow()
                    || cached.Details == null)
                {
                    return;
                }

                details = cached.Details;
            }

            if (string.IsNullOrWhiteSpace(program.EpisodeTitle))
            {
                program.EpisodeTitle = details.EpisodeTitle;
            }

            if (string.IsNullOrWhiteSpace(program.Overview))
            {
                program.Overview = details.Overview;
            }

            if (program.Genres.Count == 0 && details.Genres.Count > 0)
            {
                program.Genres = details.Genres.ToArray();
            }

            if (!program.SeasonNumber.HasValue)
            {
                program.SeasonNumber = details.SeasonNumber;
            }

            if (!program.EpisodeNumber.HasValue)
            {
                program.EpisodeNumber = details.EpisodeNumber;
            }

            if (!program.ProductionYear.HasValue)
            {
                program.ProductionYear = details.ProductionYear;
            }

            if (string.IsNullOrWhiteSpace(program.AgeRating))
            {
                program.AgeRating = details.AgeRating;
            }
        }

        public void Stop()
        {
            Task workerToWait;
            ZattooGuideDetailsProgress? progress;
            lock (syncRoot)
            {
                if (stopped)
                {
                    return;
                }

                stopped = true;
                cancellation.Cancel();
                progress = CreateProgressLocked(
                    ZattooGuideDetailsProgressKind.Stopped);
                workerToWait = worker;
            }

            Report(progress);
            try
            {
                workerToWait.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            Stop();
            disposed = true;
            signal.Dispose();
            cancellation.Dispose();
        }

        private async Task RunAsync()
        {
            var cancellationToken = cancellation.Token;
            try
            {
                while (true)
                {
                    await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var firstRequest = true;
                    while (TryTakeBatch(out var batch))
                    {
                        if (!firstRequest && requestInterval > TimeSpan.Zero)
                        {
                            await Task.Delay(requestInterval, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        firstRequest = false;
                        IReadOnlyList<ZattooProgramDetails> details;
                        try
                        {
                            details = await fetchDetails(
                                    batch.Select(item => item.Id).ToArray(),
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                            when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (ZattooException)
                        {
                            RequeueFailedBatch(batch);
                            if (retryDelay > TimeSpan.Zero)
                            {
                                await Task.Delay(retryDelay, cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            firstRequest = true;
                            continue;
                        }

                        CacheBatch(batch, details);
                    }

                    CompleteCycleIfIdle();
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private bool TryTakeBatch(out IReadOnlyList<PendingProgram> batch)
        {
            lock (syncRoot)
            {
                if (stopped || pendingPrograms.Count == 0)
                {
                    batch = Array.Empty<PendingProgram>();
                    return false;
                }

                var now = utcNow();
                CleanupIfDueLocked(now);
                var result = new List<PendingProgram>(BatchSize);
                while (pendingPrograms.Count > 0 && result.Count < BatchSize)
                {
                    var item = pendingPrograms.Min!;
                    pendingPrograms.Remove(item);
                    pendingById.Remove(item.Id);
                    if (item.ExpiresAt > now)
                    {
                        inFlightIds.Add(item.Id);
                        result.Add(item);
                    }
                    else
                    {
                        removedPrograms++;
                    }
                }

                batch = result;
                return result.Count > 0;
            }
        }

        private void CacheBatch(
            IReadOnlyList<PendingProgram> batch,
            IReadOnlyList<ZattooProgramDetails> details)
        {
            var returned = details
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var now = utcNow();
            var persistentRecords = new List<ZattooGuideDetailsCacheRecord>(batch.Count);
            ZattooGuideDetailsProgress? progress = null;
            lock (syncRoot)
            {
                foreach (var item in batch)
                {
                    inFlightIds.Remove(item.Id);
                    returned.TryGetValue(item.Id, out var detail);
                    var entry = new DetailCacheEntry(
                        item.Fingerprint,
                        detail,
                        item.ExpiresAt,
                        CalculateRefreshAfter(item, detail, now));
                    detailsById[item.Id] = entry;
                    persistentRecords.Add(CreatePersistentRecord(item.Id, entry));
                }

                processedPrograms += batch.Count;
                if (processedPrograms >= nextProgressAt)
                {
                    while (nextProgressAt <= processedPrograms)
                    {
                        nextProgressAt += ProgressInterval;
                    }

                    progress = CreateProgressLocked(
                        ZattooGuideDetailsProgressKind.Progress);
                }

                if (pendingPrograms.Count == 0
                    && inFlightIds.Count == 0
                    && cycleActive)
                {
                    cycleActive = false;
                    progress = CreateProgressLocked(
                        ZattooGuideDetailsProgressKind.Completed);
                }
            }

            persistentStore?.Append(persistentRecords);
            TryCompactPersistentCache();
            Report(progress);
        }

        private void RequeueFailedBatch(IReadOnlyList<PendingProgram> batch)
        {
            ZattooGuideDetailsProgress progress;
            lock (syncRoot)
            {
                foreach (var item in batch)
                {
                    inFlightIds.Remove(item.Id);
                    if (item.ExpiresAt <= utcNow()
                        || detailsById.ContainsKey(item.Id)
                        || pendingById.ContainsKey(item.Id))
                    {
                        continue;
                    }

                    pendingPrograms.Add(item);
                    pendingById[item.Id] = item;
                }

                failedBatches++;
                progress = CreateProgressLocked(
                    ZattooGuideDetailsProgressKind.Retrying);
            }

            Report(progress);
        }

        private void CompleteCycleIfIdle()
        {
            ZattooGuideDetailsProgress? progress = null;
            lock (syncRoot)
            {
                if (cycleActive
                    && pendingPrograms.Count == 0
                    && inFlightIds.Count == 0)
                {
                    cycleActive = false;
                    progress = CreateProgressLocked(
                        ZattooGuideDetailsProgressKind.Completed);
                }
            }

            Report(progress);
        }

        private void CleanupIfDueLocked(DateTimeOffset now)
        {
            if (now < nextCleanupAt)
            {
                return;
            }

            var expiredDetails = detailsById
                .Where(item => item.Value.ExpiresAt <= now)
                .Select(item => item.Key)
                .ToArray();
            foreach (var id in expiredDetails)
            {
                detailsById.Remove(id);
            }

            var expiredPending = pendingPrograms
                .Where(item => item.ExpiresAt <= now)
                .ToArray();
            foreach (var item in expiredPending)
            {
                pendingPrograms.Remove(item);
                pendingById.Remove(item.Id);
            }

            removedPrograms += expiredDetails.Length + expiredPending.Length;
            nextCleanupAt = now.Add(cleanupInterval);
        }

        private ZattooGuideDetailsProgress CreateProgressLocked(
            ZattooGuideDetailsProgressKind kind)
        {
            return new ZattooGuideDetailsProgress
            {
                Kind = kind,
                PendingPrograms = pendingPrograms.Count + inFlightIds.Count,
                CachedPrograms = detailsById.Count,
                ProcessedPrograms = processedPrograms,
                FailedBatches = failedBatches,
                RemovedPrograms = removedPrograms,
            };
        }

        private void Report(ZattooGuideDetailsProgress? progress)
        {
            if (progress == null || reportProgress == null)
            {
                return;
            }

            try
            {
                reportProgress(progress);
            }
            catch (Exception)
            {
                // Observability must never interrupt guide enrichment.
            }
        }

        private static ZattooGuideDetailsCacheStore? CreatePersistentStore(
            string cachePath,
            string cacheScope)
        {
            return string.IsNullOrWhiteSpace(cachePath)
                || string.IsNullOrWhiteSpace(cacheScope)
                ? null
                : new ZattooGuideDetailsCacheStore(cachePath, cacheScope);
        }

        private void LoadPersistentCache()
        {
            if (persistentStore == null)
            {
                return;
            }

            ZattooGuideDetailsCacheLoadResult loaded;
            try
            {
                loaded = persistentStore.Load();
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            var now = utcNow();
            var discarded = false;
            foreach (var record in loaded.Entries)
            {
                DateTimeOffset expiresAt;
                DateTimeOffset refreshAfter;
                try
                {
                    expiresAt = DateTimeOffset.FromUnixTimeSeconds(record.ExpiresAt);
                    refreshAfter = DateTimeOffset.FromUnixTimeSeconds(
                        record.RefreshAfter);
                }
                catch (ArgumentOutOfRangeException)
                {
                    discarded = true;
                    continue;
                }

                if (expiresAt <= now)
                {
                    discarded = true;
                    removedPrograms++;
                    continue;
                }

                detailsById[record.Id] = new DetailCacheEntry(
                    record.Fingerprint,
                    record.ToDetails(),
                    expiresAt,
                    refreshAfter);
            }

            if (discarded || loaded.NeedsCompaction)
            {
                persistentStore.Replace(CreatePersistentSnapshot());
            }
        }

        private void TryCompactPersistentCache()
        {
            if (persistentStore == null)
            {
                return;
            }

            ZattooGuideDetailsCacheRecord[] snapshot;
            lock (syncRoot)
            {
                if (!persistentStore.ShouldCompact(detailsById.Count))
                {
                    return;
                }

                snapshot = CreatePersistentSnapshotLocked();
            }

            persistentStore.Replace(snapshot);
        }

        private ZattooGuideDetailsCacheRecord[] CreatePersistentSnapshot()
        {
            lock (syncRoot)
            {
                return CreatePersistentSnapshotLocked();
            }
        }

        private ZattooGuideDetailsCacheRecord[] CreatePersistentSnapshotLocked()
        {
            return detailsById
                .Select(item => CreatePersistentRecord(item.Key, item.Value))
                .ToArray();
        }

        private static ZattooGuideDetailsCacheRecord CreatePersistentRecord(
            string id,
            DetailCacheEntry entry)
        {
            return new ZattooGuideDetailsCacheRecord
            {
                Id = id,
                Fingerprint = entry.Fingerprint,
                ExpiresAt = entry.ExpiresAt.ToUnixTimeSeconds(),
                RefreshAfter = entry.RefreshAfter.ToUnixTimeSeconds(),
                HasDetails = entry.Details != null,
                EpisodeTitle = entry.Details?.EpisodeTitle,
                Overview = entry.Details?.Overview,
                Genres = entry.Details?.Genres.ToArray() ?? Array.Empty<string>(),
                SeasonNumber = entry.Details?.SeasonNumber,
                EpisodeNumber = entry.Details?.EpisodeNumber,
                ProductionYear = entry.Details?.ProductionYear,
                AgeRating = entry.Details?.AgeRating,
            };
        }

        private static HashSet<string> FindNextProgramIds(
            IEnumerable<ZattooProgram> programs,
            DateTimeOffset now)
        {
            var horizon = now.Add(NextProgramHorizon);
            return new HashSet<string>(programs
                .Where(program => program.StartDate > now
                    && program.StartDate <= horizon)
                .GroupBy(program => program.ChannelId, StringComparer.Ordinal)
                .Select(group => group
                    .OrderBy(program => program.StartDate)
                    .ThenBy(program => program.Id, StringComparer.Ordinal)
                    .First()
                    .Id),
                StringComparer.Ordinal);
        }

        private static int CalculatePriority(
            ZattooProgram program,
            HashSet<string> favoriteChannelIds,
            HashSet<string> nextProgramIds,
            DateTimeOffset now)
        {
            if (program.StartDate <= now && program.EndDate > now)
            {
                return PriorityCurrent;
            }

            if (nextProgramIds.Contains(program.Id))
            {
                return PriorityNext;
            }

            if (favoriteChannelIds.Contains(program.ChannelId))
            {
                return PriorityFavorite;
            }

            return program.StartDate <= now.Add(NearTermHorizon)
                ? PriorityNearTerm
                : PriorityBackground;
        }

        private static string CreateProgramFingerprint(ZattooProgram program)
        {
            var source = string.Join(
                "\u001f",
                new[]
                {
                    program.Id,
                    program.ChannelId,
                    program.Name,
                    program.EpisodeTitle ?? string.Empty,
                    program.Overview ?? string.Empty,
                    program.StartDate.ToUnixTimeSeconds().ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    program.EndDate.ToUnixTimeSeconds().ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    string.Join(
                        "\u001e",
                        program.Genres.OrderBy(value => value, StringComparer.Ordinal)),
                    program.SeasonNumber?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    program.EpisodeNumber?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    program.ImageUrl ?? string.Empty,
                });
            using (var hash = SHA256.Create())
            {
                return BitConverter.ToString(
                        hash.ComputeHash(Encoding.UTF8.GetBytes(source)))
                    .Replace("-", string.Empty);
            }
        }

        private static DateTimeOffset CalculateRefreshAfter(
            PendingProgram program,
            ZattooProgramDetails? details,
            DateTimeOffset now)
        {
            if (HasCompleteDescription(details))
            {
                return DateTimeOffset.MaxValue;
            }

            var beforeStart = program.StartDate.Subtract(DetailLeadTime);
            var retryAt = beforeStart > now
                ? beforeStart
                : now.Add(IncompleteDetailRetry);
            return retryAt < program.ExpiresAt ? retryAt : program.ExpiresAt;
        }

        private static bool HasCompleteDescription(ZattooProgramDetails? details)
        {
            return !string.IsNullOrWhiteSpace(details?.Overview);
        }

        private static bool NeedsDetails(ZattooProgram program)
        {
            return string.IsNullOrWhiteSpace(program.Overview)
                || program.Genres.Count == 0;
        }

        private sealed class DetailCacheEntry
        {
            public DetailCacheEntry(
                string fingerprint,
                ZattooProgramDetails? details,
                DateTimeOffset expiresAt,
                DateTimeOffset refreshAfter)
            {
                Fingerprint = fingerprint;
                Details = details;
                ExpiresAt = expiresAt;
                RefreshAfter = refreshAfter;
            }

            public string Fingerprint { get; }

            public ZattooProgramDetails? Details { get; }

            public DateTimeOffset ExpiresAt { get; set; }

            public DateTimeOffset RefreshAfter { get; }
        }

        private sealed class PendingProgram
        {
            public PendingProgram(
                string id,
                string channelId,
                string fingerprint,
                int priority,
                DateTimeOffset startDate,
                DateTimeOffset expiresAt)
            {
                Id = id;
                ChannelId = channelId;
                Fingerprint = fingerprint;
                Priority = priority;
                StartDate = startDate;
                ExpiresAt = expiresAt;
            }

            public string Id { get; }

            public string ChannelId { get; }

            public string Fingerprint { get; }

            public int Priority { get; }

            public DateTimeOffset StartDate { get; }

            public DateTimeOffset ExpiresAt { get; }

            public PendingProgram Merge(PendingProgram other)
            {
                return new PendingProgram(
                    Id,
                    ChannelId,
                    Fingerprint,
                    Math.Min(Priority, other.Priority),
                    StartDate <= other.StartDate ? StartDate : other.StartDate,
                    ExpiresAt >= other.ExpiresAt ? ExpiresAt : other.ExpiresAt);
            }
        }

        private sealed class PendingProgramComparer : IComparer<PendingProgram>
        {
            public static PendingProgramComparer Instance { get; } =
                new PendingProgramComparer();

            public int Compare(PendingProgram? left, PendingProgram? right)
            {
                if (ReferenceEquals(left, right))
                {
                    return 0;
                }

                if (left == null)
                {
                    return -1;
                }

                if (right == null)
                {
                    return 1;
                }

                var priority = left.Priority.CompareTo(right.Priority);
                if (priority != 0)
                {
                    return priority;
                }

                var start = left.StartDate.CompareTo(right.StartDate);
                return start != 0
                    ? start
                    : StringComparer.Ordinal.Compare(left.Id, right.Id);
            }

            public bool Equals(PendingProgram left, PendingProgram right)
            {
                return left.Priority == right.Priority
                    && string.Equals(
                        left.Fingerprint,
                        right.Fingerprint,
                        StringComparison.Ordinal)
                    && left.StartDate == right.StartDate
                    && left.ExpiresAt == right.ExpiresAt;
            }
        }
    }
}
