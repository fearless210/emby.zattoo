using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Zattoo
{
    internal sealed class ZattooGuideService : IDisposable
    {
        private const long WindowSeconds = 5 * 60 * 60;
        private static readonly TimeSpan MaximumRange = TimeSpan.FromDays(14);

        private readonly TimeSpan cacheDuration;
        private readonly Func<long, long, CancellationToken, Task<string>> fetchWindow;
        private readonly ZattooGuideDetailsService? detailsService;
        private readonly SemaphoreSlim requestLock = new SemaphoreSlim(1, 1);
        private readonly object cacheLock = new object();
        private readonly Dictionary<long, GuideCacheEntry> cache
            = new Dictionary<long, GuideCacheEntry>();
        private bool disposed;
        private HashSet<string>? importedChannelIds;

        public ZattooGuideService(
            TimeSpan cacheDuration,
            Func<long, long, CancellationToken, Task<string>> fetchWindow,
            ZattooGuideDetailsService? detailsService = null)
        {
            if (cacheDuration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(cacheDuration));
            }

            this.cacheDuration = cacheDuration;
            this.fetchWindow = fetchWindow
                ?? throw new ArgumentNullException(nameof(fetchWindow));
            this.detailsService = detailsService;
        }

        public async Task<IReadOnlyList<ZattooProgram>> GetProgramsAsync(
            IReadOnlyCollection<string> channelIds,
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            IReadOnlyCollection<string> favoriteChannelIds,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (channelIds == null)
            {
                throw new ArgumentNullException(nameof(channelIds));
            }

            if (favoriteChannelIds == null)
            {
                throw new ArgumentNullException(nameof(favoriteChannelIds));
            }

            var requestedStart = startTime.ToUniversalTime();
            var requestedEnd = endTime.ToUniversalTime();
            if (requestedEnd <= requestedStart)
            {
                throw new ArgumentException(
                    "The guide end time must be later than its start time.",
                    nameof(endTime));
            }

            if (requestedEnd - requestedStart > MaximumRange)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(endTime),
                    "The guide range cannot exceed 14 days.");
            }

            var requestedChannels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var channelId in channelIds)
            {
                if (string.IsNullOrWhiteSpace(channelId))
                {
                    throw new ArgumentException(
                        "Guide channel IDs cannot be empty.",
                        nameof(channelIds));
                }

                requestedChannels.Add(channelId.Trim());
            }

            if (requestedChannels.Count == 0)
            {
                return Array.Empty<ZattooProgram>();
            }

            RemoveExpiredEntries(DateTimeOffset.UtcNow);

            var result = new List<ZattooProgram>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var windowStart = AlignWindow(requestedStart.ToUnixTimeSeconds());
            while (DateTimeOffset.FromUnixTimeSeconds(windowStart) < requestedEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var windowResult = await GetWindowAsync(windowStart, cancellationToken)
                    .ConfigureAwait(false);
                var window = windowResult.Window;
                if (windowResult.WasLoaded)
                {
                    detailsService?.QueuePrograms(
                        window.ProgramsByChannel.Values.SelectMany(programs => programs),
                        favoriteChannelIds,
                        requestedStart,
                        requestedEnd);
                }

                foreach (var channelId in requestedChannels)
                {
                    if (!window.ProgramsByChannel.TryGetValue(channelId, out var programs))
                    {
                        continue;
                    }

                    foreach (var program in programs)
                    {
                        if (program.EndDate <= requestedStart
                            || program.StartDate >= requestedEnd)
                        {
                            continue;
                        }

                        var identity = program.ChannelId
                            + "\n"
                            + program.Id
                            + "\n"
                            + program.StartDate.UtcDateTime.Ticks.ToString(
                                CultureInfo.InvariantCulture);
                        if (seen.Add(identity))
                        {
                            var copy = CopyProgram(program);
                            detailsService?.ApplyDetails(copy);
                            result.Add(copy);
                        }
                    }
                }

                windowStart = checked(windowStart + WindowSeconds);
            }

            return result
                .OrderBy(program => program.StartDate)
                .ThenBy(program => program.ChannelId, StringComparer.Ordinal)
                .ThenBy(program => program.Id, StringComparer.Ordinal)
                .ToArray();
        }

        public void Invalidate()
        {
            lock (cacheLock)
            {
                cache.Clear();
            }
        }

        public void SetImportedChannelIds(IReadOnlyCollection<string> channelIds)
        {
            ThrowIfDisposed();
            if (channelIds == null)
            {
                throw new ArgumentNullException(nameof(channelIds));
            }

            var normalized = new HashSet<string>(StringComparer.Ordinal);
            foreach (var channelId in channelIds)
            {
                if (string.IsNullOrWhiteSpace(channelId))
                {
                    throw new ArgumentException(
                        "Imported guide channel IDs cannot be empty.",
                        nameof(channelIds));
                }

                normalized.Add(channelId.Trim());
            }

            var changed = false;
            lock (cacheLock)
            {
                if (importedChannelIds == null
                    || !importedChannelIds.SetEquals(normalized))
                {
                    importedChannelIds = normalized;
                    cache.Clear();
                    changed = true;
                }
            }

            if (changed)
            {
                detailsService?.RestrictToChannels(normalized);
            }
        }

        public void StopGuideEnrichment()
        {
            detailsService?.Stop();
        }

        public void PrioritizeGuideDetails(
            string channelId,
            DateTimeOffset now)
        {
            if (detailsService == null)
            {
                return;
            }

            var horizon = now.AddHours(8);
            ZattooProgram[] candidates;
            lock (cacheLock)
            {
                candidates = cache.Values
                    .Where(entry => entry.ExpiresAt > now)
                    .SelectMany(entry =>
                        entry.Window.ProgramsByChannel.TryGetValue(
                            channelId,
                            out var programs)
                            ? programs
                            : Array.Empty<ZattooProgram>())
                    .Where(program => program.EndDate > now
                        && program.StartDate <= horizon)
                    .GroupBy(program => program.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
            }

            detailsService.PrioritizePrograms(candidates);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            detailsService?.Dispose();
            Invalidate();
            requestLock.Dispose();
        }

        private async Task<GuideWindowResult> GetWindowAsync(
            long windowStart,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            lock (cacheLock)
            {
                if (cache.TryGetValue(windowStart, out var cached)
                    && cached.ExpiresAt > now)
                {
                    return new GuideWindowResult(cached.Window, false);
                }

                cache.Remove(windowStart);
            }

            await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                now = DateTimeOffset.UtcNow;
                lock (cacheLock)
                {
                    if (cache.TryGetValue(windowStart, out var cached)
                        && cached.ExpiresAt > now)
                    {
                        return new GuideWindowResult(cached.Window, false);
                    }

                    cache.Remove(windowStart);
                }

                var content = await fetchWindow(
                        windowStart,
                        checked(windowStart + WindowSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);
                HashSet<string>? channelFilter;
                lock (cacheLock)
                {
                    channelFilter = importedChannelIds == null
                        ? null
                        : new HashSet<string>(
                            importedChannelIds,
                            StringComparer.Ordinal);
                }

                var window = ParseGuide(content, channelFilter);
                lock (cacheLock)
                {
                    cache[windowStart] = new GuideCacheEntry(
                        window,
                        DateTimeOffset.UtcNow.Add(cacheDuration));
                }

                return new GuideWindowResult(window, true);
            }
            finally
            {
                requestLock.Release();
            }
        }

        private static long AlignWindow(long unixTime)
        {
            var quotient = Math.Floor((double)unixTime / WindowSeconds);
            return checked((long)quotient * WindowSeconds);
        }

        private void RemoveExpiredEntries(DateTimeOffset now)
        {
            lock (cacheLock)
            {
                var expired = cache
                    .Where(entry => entry.Value.ExpiresAt <= now)
                    .Select(entry => entry.Key)
                    .ToArray();
                foreach (var key in expired)
                {
                    cache.Remove(key);
                }
            }
        }

        private static GuideWindow ParseGuide(
            string content,
            HashSet<string>? channelFilter = null)
        {
            JsonElement root;
            try
            {
                using (var document = JsonDocument.Parse(content))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new ZattooProtocolException(
                            "Zattoo returned an invalid guide response.");
                    }

                    root = document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                throw new ZattooProtocolException(
                    "Zattoo returned malformed JSON for guide data.");
            }

            if ((root.TryGetProperty("success", out var success)
                    && !ReadBoolean(success))
                || !root.TryGetProperty("channels", out var channels)
                || (channels.ValueKind != JsonValueKind.Object
                    && channels.ValueKind != JsonValueKind.Array))
            {
                throw new ZattooProtocolException(
                    "The Zattoo guide response is invalid.");
            }

            var programsByChannel = new Dictionary<string, IReadOnlyList<ZattooProgram>>(
                StringComparer.Ordinal);
            if (channels.ValueKind == JsonValueKind.Object)
            {
                foreach (var channel in channels.EnumerateObject())
                {
                    if (channelFilter != null
                        && !channelFilter.Contains(channel.Name))
                    {
                        continue;
                    }

                    AddChannel(programsByChannel, channel.Name, channel.Value);
                }
            }
            else
            {
                foreach (var channel in channels.EnumerateArray())
                {
                    if (channel.ValueKind != JsonValueKind.Object
                        || !channel.TryGetProperty("programs", out var programs))
                    {
                        continue;
                    }

                    var channelId = ReadString(channel, "cid");
                    if (channelFilter == null || channelFilter.Contains(channelId))
                    {
                        AddChannel(programsByChannel, channelId, programs);
                    }
                }
            }

            return new GuideWindow(programsByChannel);
        }

        internal static IReadOnlyList<ZattooProgram> ParseProgramsForSurvey(
            string content)
        {
            return ParseGuide(content)
                .ProgramsByChannel
                .Values
                .SelectMany(programs => programs)
                .ToArray();
        }

        private static void AddChannel(
            IDictionary<string, IReadOnlyList<ZattooProgram>> programsByChannel,
            string channelId,
            JsonElement programs)
        {
            if (string.IsNullOrWhiteSpace(channelId)
                || programs.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var result = new List<ZattooProgram>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in programs.EnumerateArray())
            {
                if (!TryParseProgram(channelId, element, out var program))
                {
                    continue;
                }

                var identity = program.Id
                    + "\n"
                    + program.StartDate.UtcDateTime.Ticks.ToString(
                        CultureInfo.InvariantCulture);
                if (seen.Add(identity))
                {
                    result.Add(program);
                }
            }

            programsByChannel[channelId] = result
                .OrderBy(program => program.StartDate)
                .ThenBy(program => program.Id, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool TryParseProgram(
            string channelId,
            JsonElement element,
            out ZattooProgram program)
        {
            program = new ZattooProgram();
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var id = ReadIdentifier(element, "id");
            var title = ReadString(element, "t").Trim();
            var episodeTitle = ReadString(element, "et").Trim();
            var start = ReadNullableInt64(element, "s");
            var end = ReadNullableInt64(element, "e");
            if (string.IsNullOrWhiteSpace(id)
                || !start.HasValue
                || !end.HasValue
                || end.Value <= start.Value
                || (title.Length == 0 && episodeTitle.Length == 0))
            {
                return false;
            }

            DateTimeOffset startDate;
            DateTimeOffset endDate;
            try
            {
                startDate = DateTimeOffset.FromUnixTimeSeconds(start.Value);
                endDate = DateTimeOffset.FromUnixTimeSeconds(end.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            program = new ZattooProgram
            {
                Id = id,
                ChannelId = channelId,
                Name = title.Length == 0 ? episodeTitle : title,
                EpisodeTitle = EmptyToNull(episodeTitle),
                Overview = EmptyToNull(ReadString(element, "d")),
                StartDate = startDate,
                EndDate = endDate,
                Genres = ReadStringArray(element, "g"),
                SeasonNumber = ReadNonNegativeInt32(element, "s_no"),
                EpisodeNumber = ReadNonNegativeInt32(element, "e_no"),
                ImageUrl = BuildProgramImageUrl(
                    ReadString(element, "i_url"),
                    ReadString(element, "i_t")),
            };
            return true;
        }

        private static ZattooProgram CopyProgram(ZattooProgram source)
        {
            return new ZattooProgram
            {
                Id = source.Id,
                ChannelId = source.ChannelId,
                Name = source.Name,
                EpisodeTitle = source.EpisodeTitle,
                Overview = source.Overview,
                StartDate = source.StartDate,
                EndDate = source.EndDate,
                Genres = source.Genres.ToArray(),
                SeasonNumber = source.SeasonNumber,
                EpisodeNumber = source.EpisodeNumber,
                ImageUrl = source.ImageUrl,
            };
        }

        private static string? BuildProgramImageUrl(
            string imageUrl,
            string imageToken)
        {
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                var normalized = imageUrl.Trim();
                if (normalized.StartsWith("//", StringComparison.Ordinal))
                {
                    normalized = "https:" + normalized;
                }

                if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute))
                {
                    if (absolute.Scheme == Uri.UriSchemeHttp)
                    {
                        return new UriBuilder(absolute)
                        {
                            Scheme = Uri.UriSchemeHttps,
                            Port = -1,
                        }.Uri.AbsoluteUri;
                    }

                    if (absolute.Scheme == Uri.UriSchemeHttps)
                    {
                        return absolute.AbsoluteUri;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(imageToken))
            {
                return null;
            }

            return "https://images.zattic.com/cms/"
                + Uri.EscapeDataString(imageToken.Trim())
                + "/format_480x360.jpg";
        }

        private static string ReadString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static bool ReadBoolean(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            return element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out var numeric)
                && numeric != 0;
        }

        private static int? ReadNonNegativeInt32(
            JsonElement element,
            string propertyName)
        {
            var value = ReadNullableInt32(element, propertyName);
            return value >= 0 ? value : null;
        }

        private static int? ReadNullableInt32(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out var numeric))
            {
                return numeric;
            }

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out numeric))
            {
                return numeric;
            }

            return null;
        }

        private static long? ReadNullableInt64(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number
                && property.TryGetInt64(out var numeric))
            {
                return numeric;
            }

            if (property.ValueKind == JsonValueKind.String
                && long.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out numeric))
            {
                return numeric;
            }

            return null;
        }

        private static string ReadIdentifier(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return string.Empty;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString()?.Trim() ?? string.Empty;
            }

            return property.ValueKind == JsonValueKind.Number
                && property.TryGetInt64(out var numeric)
                ? numeric.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static IReadOnlyList<string> ReadStringArray(
            JsonElement element,
            string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value!))
                {
                    values.Add(value!);
                }
            }

            return values;
        }

        private static string? EmptyToNull(string value)
        {
            var normalized = value.Trim();
            return normalized.Length == 0 ? null : normalized;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ZattooGuideService));
            }
        }

        private sealed class GuideWindow
        {
            public GuideWindow(
                IReadOnlyDictionary<string, IReadOnlyList<ZattooProgram>> programsByChannel)
            {
                ProgramsByChannel = programsByChannel;
            }

            public IReadOnlyDictionary<string, IReadOnlyList<ZattooProgram>> ProgramsByChannel
            {
                get;
            }
        }

        private sealed class GuideWindowResult
        {
            public GuideWindowResult(GuideWindow window, bool wasLoaded)
            {
                Window = window;
                WasLoaded = wasLoaded;
            }

            public GuideWindow Window { get; }

            public bool WasLoaded { get; }
        }

        private sealed class GuideCacheEntry
        {
            public GuideCacheEntry(GuideWindow window, DateTimeOffset expiresAt)
            {
                Window = window;
                ExpiresAt = expiresAt;
            }

            public GuideWindow Window { get; }

            public DateTimeOffset ExpiresAt { get; }
        }
    }
}
