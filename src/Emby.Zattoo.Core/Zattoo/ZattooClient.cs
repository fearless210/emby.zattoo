using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Infrastructure;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Zattoo
{
    /// <summary>Independent implementation of the observed Zattoo session/channel protocol.</summary>
    public sealed class ZattooClient : IZattooClient
    {
        private const string AppTokenPath = "/token.json";
        private const string LoginPagePath = "/login";
        private const string HelloPath = "/zapi/v3/session/hello";
        private const string SessionPath = "/zapi/v3/session";
        private const string AccountLoginPath = "/zapi/v3/account/login";
        private const string FavoritesPath = "/zapi/channels/favorites";

        private static readonly Regex InlineAppTokenRegex = new Regex(
            @"window\.appToken\s*=\s*['\""'](?<value>[^'\""']+)['\""']",
            RegexOptions.CultureInvariant);

        private static readonly Regex AppScriptRegex = new Regex(
            @"src\s*=\s*['\""'](?<value>/[^'\""']*app-[^'\""']+\.js(?:\?[^'\""']*)?)['\""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TokenJsonRegex = new Regex(
            @"['\""'](?<value>/?[^'\""']*token-[^'\""']+\.json(?:\?[^'\""']*)?)['\""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly ZattooClientOptions options;
        private readonly IZattooTransport transport;
        private readonly SemaphoreSlim sessionLock = new SemaphoreSlim(1, 1);
        private readonly ZattooGuideService guideService;
        private readonly object stateLock = new object();
        private readonly Dictionary<string, ZattooChannel> channelsById
            = new Dictionary<string, ZattooChannel>(StringComparer.Ordinal);
        private ZattooSessionInfo? sessionInfo;
        private bool disposed;

        public ZattooClient(ZattooClientOptions options)
            : this(options, CreateTransport(options))
        {
        }

        public ZattooClient(ZattooClientOptions options, IZattooTransport transport)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            options.Validate();
            ZattooGuideDetailsService? detailsService = null;
            if (options.EnableBackgroundGuideDetails)
            {
                detailsService = new ZattooGuideDetailsService(
                    options.GuideDetailsRequestInterval,
                    options.GuideDetailsRetryDelay,
                    LoadProgramDetailsBatchAsync,
                    options.GuideDetailsProgress,
                    options.GuideDetailsCachePath,
                    options.GuideDetailsCacheScope);
            }

            guideService = new ZattooGuideService(
                options.GuideCacheDuration,
                LoadGuideWindowContentAsync,
                detailsService);
        }

        public bool IsAuthenticated
        {
            get
            {
                lock (stateLock)
                {
                    return sessionInfo?.IsActive == true;
                }
            }
        }

        public DateTimeOffset? SessionCreatedAt
        {
            get
            {
                lock (stateLock)
                {
                    return sessionInfo?.CreatedAt;
                }
            }
        }

        public ZattooSessionInfo? SessionInfo
        {
            get
            {
                lock (stateLock)
                {
                    return sessionInfo == null ? null : CopySessionInfo(sessionInfo);
                }
            }
        }

        public Task LoginAsync(CancellationToken cancellationToken = default)
        {
            return EnsureAuthenticatedAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<ZattooChannel>> GetChannelsAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

            var favorites = await LoadFavoritesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (favorites == null)
            {
                // A failed favorites request may also have invalidated the
                // session before the catalogue could be requested.
                await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            }

            var channelsResponse = await SendAuthenticatedWithRetryAsync(
                token => transport.GetAsync(BuildChannelsPath(), token),
                "loading channels",
                cancellationToken).ConfigureAwait(false);

            var channels = ParseChannels(
                channelsResponse.Content,
                favorites ?? new HashSet<string>(StringComparer.Ordinal));
            var statistics = ZattooStreamStatistics.Calculate(channels);
            var maximumPlayableHeight = channels
                .SelectMany(channel => channel.Qualities)
                .Where(quality => quality.IsAvailable && !quality.DrmRequired)
                .Where(quality => quality.Height.HasValue)
                .Select(quality => quality.Height)
                .DefaultIfEmpty()
                .Max();
            lock (stateLock)
            {
                channelsById.Clear();
                foreach (var channel in channels)
                {
                    channelsById[channel.Id] = channel;
                }

                if (sessionInfo != null)
                {
                    sessionInfo.FavoritesAvailable = favorites != null;
                    sessionInfo.PlayableChannelCount =
                        statistics.ChannelsWithNonDrmStreams;
                    sessionInfo.DrmOnlyChannelCount = statistics.DrmOnlyChannels;
                    sessionInfo.UnavailableChannelCount =
                        statistics.ChannelsWithoutAvailableStreams;
                    sessionInfo.MaximumPlayableHeight = maximumPlayableHeight;
                }
            }

            return channels;
        }

        public async Task<IReadOnlyList<ZattooProgram>> GetProgramsAsync(
            IReadOnlyCollection<string> channelIds,
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            string[] favoriteChannelIds;
            lock (stateLock)
            {
                favoriteChannelIds = channelsById.Values
                    .Where(channel => channel.IsFavorite)
                    .Select(channel => channel.Id)
                    .ToArray();
            }

            return await guideService.GetProgramsAsync(
                    channelIds,
                    startTime,
                    endTime,
                    favoriteChannelIds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public void SetImportedGuideChannels(
            IReadOnlyCollection<string> channelIds)
        {
            ThrowIfDisposed();
            guideService.SetImportedChannelIds(channelIds);
        }

        public async Task<IReadOnlyList<ZattooProgramDetails>> GetProgramDetailsAsync(
            IReadOnlyCollection<string> programIds,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (programIds == null)
            {
                throw new ArgumentNullException(nameof(programIds));
            }

            var normalizedIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var programId in programIds)
            {
                if (string.IsNullOrWhiteSpace(programId))
                {
                    throw new ArgumentException(
                        "Program detail IDs cannot be empty.",
                        nameof(programIds));
                }

                var normalized = programId.Trim();
                if (seen.Add(normalized))
                {
                    normalizedIds.Add(normalized);
                }
            }

            if (normalizedIds.Count == 0)
            {
                return Array.Empty<ZattooProgramDetails>();
            }

            if (normalizedIds.Count > 20)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(programIds),
                    "At most 20 program details can be requested at once.");
            }

            return await LoadProgramDetailsBatchAsync(
                    normalizedIds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<ZattooGuideEndpointComparison> CompareGuideEndpointsAsync(
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var start = startTime.ToUniversalTime();
            var end = endTime.ToUniversalTime();
            if (end <= start)
            {
                throw new ArgumentException(
                    "The guide comparison end time must be later than its start time.",
                    nameof(endTime));
            }

            if (end - start > TimeSpan.FromHours(6))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(endTime),
                    "The guide comparison range cannot exceed six hours.");
            }

            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            var startSeconds = start.ToUnixTimeSeconds();
            var endSeconds = end.ToUnixTimeSeconds();
            var version2 = await LoadGuideEndpointForSurveyAsync(
                    BuildLegacyGuidePath(startSeconds, endSeconds),
                    "loading legacy guide data for comparison",
                    cancellationToken)
                .ConfigureAwait(false);
            var version3 = await LoadGuideEndpointForSurveyAsync(
                    BuildGuidePath(startSeconds, endSeconds),
                    "loading current guide data for comparison",
                    cancellationToken)
                .ConfigureAwait(false);

            return CreateGuideEndpointComparison(start, end, version2, version3);
        }

        public void StopGuideEnrichment()
        {
            guideService.StopGuideEnrichment();
        }

        public void PrioritizeGuideDetails(string channelId)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentException(
                    "A channel ID is required to prioritize guide details.",
                    nameof(channelId));
            }

            guideService.PrioritizeGuideDetails(
                channelId.Trim(),
                DateTimeOffset.UtcNow);
        }

        private async Task<IReadOnlyList<ZattooProgramDetails>> LoadProgramDetailsBatchAsync(
            IReadOnlyList<string> normalizedIds,
            CancellationToken cancellationToken)
        {
            var content = await LoadProgramDetailsContentAsync(
                    normalizedIds,
                    cancellationToken)
                .ConfigureAwait(false);
            return ParseProgramDetails(content);
        }

        private async Task<string> LoadProgramDetailsContentAsync(
            IReadOnlyList<string> normalizedIds,
            CancellationToken cancellationToken)
        {
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            var response = await SendAuthenticatedWithRetryAsync(
                token => transport.GetAsync(
                    BuildProgramDetailsPath(normalizedIds),
                    token),
                "loading program details",
                cancellationToken).ConfigureAwait(false);
            return response.Content;
        }

        /// <summary>
        /// Reports which fields the account actually receives, so features can be
        /// built on observed data instead of assumptions. No value is collected.
        /// </summary>
        public async Task<ZattooFieldInventory> SurveyFieldsAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            var sections = new List<ZattooFieldSection>();

            var channelsResponse = await SendAuthenticatedWithRetryAsync(
                token => transport.GetAsync(BuildChannelsPath(), token),
                "loading channels for a field survey",
                cancellationToken).ConfigureAwait(false);
            sections.Add(ZattooFieldSurvey.Analyze("channels", channelsResponse.Content));

            var favoritesResponse = await SendAuthenticatedWithRetryAsync(
                token => transport.GetAsync(FavoritesPath, token),
                "loading favorites for a field survey",
                cancellationToken).ConfigureAwait(false);
            sections.Add(ZattooFieldSurvey.Analyze("favorites", favoritesResponse.Content));

            var windowStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var guideContent = await LoadGuideWindowContentAsync(
                    windowStart,
                    windowStart + (5 * 60 * 60),
                    cancellationToken)
                .ConfigureAwait(false);
            sections.Add(ZattooFieldSurvey.Analyze("guide", guideContent));

            var programIds = ZattooGuideService.ParseProgramsForSurvey(guideContent)
                .Select(program => program.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToArray();
            if (programIds.Length > 0)
            {
                var detailsContent = await LoadProgramDetailsContentAsync(
                        programIds,
                        cancellationToken)
                    .ConfigureAwait(false);
                sections.Add(
                    ZattooFieldSurvey.Analyze("program details", detailsContent));
            }

            return new ZattooFieldInventory { Sections = sections };
        }

        public async Task<IReadOnlyList<ZattooStream>> GetStreamOptionsAsync(
            string channelId,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var channel = await GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
            var result = new List<ZattooStream>();

            foreach (var quality in channel.Qualities)
            {
                if (!quality.IsAvailable)
                {
                    continue;
                }

                if (quality.DrmRequired)
                {
                    result.Add(CreateUnsupportedDrmStream(quality));
                    continue;
                }

                result.Add(
                    await RequestStreamAsync(
                            channel.Id,
                            quality,
                            ZattooStreamFormat.Dash,
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            return result;
        }

        public async Task<ZattooStream> GetStreamAsync(
            string channelId,
            ZattooPreferredQuality preferredQuality = ZattooPreferredQuality.Auto,
            CancellationToken cancellationToken = default)
        {
            return await GetStreamAsync(
                    channelId,
                    preferredQuality,
                    ZattooStreamFormat.Dash,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<ZattooStream> GetStreamAsync(
            string channelId,
            ZattooPreferredQuality preferredQuality,
            ZattooStreamFormat preferredFormat,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var channel = await GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
            var quality = ZattooQualitySelector.SelectBest(channel.Qualities, preferredQuality);
            if (quality == null)
            {
                if (channel.Qualities.Any(
                    item => item.IsAvailable && item.DrmRequired))
                {
                    throw new ZattooDrmRequiredException(
                        "The selected Zattoo channel is available only with DRM.");
                }

                throw new ZattooStreamUnavailableException(
                    "The selected Zattoo channel has no available stream quality.");
            }

            var stream = await RequestStreamAsync(
                    channel.Id,
                    quality,
                    preferredFormat,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stream.DrmRequired)
            {
                throw new ZattooDrmRequiredException(
                    "Zattoo returned a DRM-protected stream for the selected channel.");
            }

            if (!stream.IsAvailable)
            {
                throw new ZattooStreamUnavailableException(
                    "Zattoo did not return a usable stream for the selected channel.");
            }

            return stream;
        }

        public void Invalidate()
        {
            lock (stateLock)
            {
                sessionInfo = null;
                channelsById.Clear();
            }

            guideService.Invalidate();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            guideService.StopGuideEnrichment();
            disposed = true;
            Invalidate();
            guideService.Dispose();
            sessionLock.Dispose();
            transport.Dispose();
        }

        private static IZattooTransport CreateTransport(ZattooClientOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            options.Validate();
            return new ZattooHttpTransport(options);
        }

        private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (IsAuthenticated)
            {
                return;
            }

            await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsAuthenticated)
                {
                    await AuthenticateLockedAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                sessionLock.Release();
            }
        }

        private async Task AuthenticateLockedAsync(CancellationToken cancellationToken)
        {
            Invalidate();
            transport.ResetSession(options.DeviceId);

            var appToken = await LoadAppTokenAsync(cancellationToken).ConfigureAwait(false);
            var helloResponse = await transport.PostFormAsync(
                HelloPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["lang"] = options.Language,
                    ["app_version"] = options.ApplicationVersion,
                    ["client_app_token"] = appToken,
                    ["uuid"] = options.DeviceId,
                    ["format"] = "json",
                },
                cancellationToken).ConfigureAwait(false);

            EnsureSuccess(helloResponse, "initializing the Zattoo session", authentication: true);
            var hello = ParseObject(helloResponse.Content, "session initialization");
            if (!ReadBoolean(hello, "active"))
            {
                throw new ZattooAuthenticationException("Zattoo did not activate the new session.");
            }

            var sessionResponse = await transport.GetAsync(SessionPath, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(sessionResponse, "reading the Zattoo session", authentication: true);
            var session = ParseObject(sessionResponse.Content, "session response");
            if (!ReadBoolean(session, "active"))
            {
                throw new ZattooAuthenticationException("The Zattoo session is inactive.");
            }

            if (!HasAccount(session))
            {
                // Keep the anonymous hello cookie for account login. Current web-client
                // implementations bind the login request to this initialized session.
                session = await LoginAccountAsync(cancellationToken).ConfigureAwait(false);
            }

            var powerGuideHash = ReadString(session, "power_guide_hash");
            if (!HasAccount(session) || string.IsNullOrWhiteSpace(powerGuideHash))
            {
                throw new ZattooAuthenticationException(
                    "Zattoo authentication succeeded without usable account session data.");
            }

            var account = session.GetProperty("account");
            var nonlive = session.TryGetProperty("nonlive", out var nonliveValue)
                && nonliveValue.ValueKind == JsonValueKind.Object
                ? nonliveValue
                : default;
            var recordingNumberLimit = nonlive.ValueKind == JsonValueKind.Object
                ? ReadNullableInt32(nonlive, "recording_number_limit") ?? 0
                : 0;
            var explicitConcurrentStreamLimit =
                ReadConcurrentStreamLimit(session, account, nonlive);
            var newSession = new ZattooSessionInfo
            {
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                CountryCode = ReadString(session, "current_country"),
                ServiceCountry = ReadString(account, "service_country"),
                ReplayAvailable = nonlive.ValueKind == JsonValueKind.Object
                    && string.Equals(
                        ReadString(nonlive, "replay_availability"),
                        "available",
                        StringComparison.OrdinalIgnoreCase),
                RecordingNumberLimit = Math.Max(0, recordingNumberLimit),
                MaximumConcurrentStreams = explicitConcurrentStreamLimit
                    ?? InferConcurrentStreamLimit(recordingNumberLimit),
                ConcurrentStreamLimitIsInferred =
                    !explicitConcurrentStreamLimit.HasValue,
                PowerGuideHash = powerGuideHash,
            };

            lock (stateLock)
            {
                sessionInfo = newSession;
            }
        }

        private async Task<JsonElement> LoginAccountAsync(CancellationToken cancellationToken)
        {
            var loginResponse = await transport.PostFormAsync(
                AccountLoginPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["login"] = options.Username,
                    ["password"] = options.Password,
                    ["format"] = "json",
                    ["remember"] = "true",
                },
                cancellationToken).ConfigureAwait(false);

            EnsureSuccess(loginResponse, "authenticating the Zattoo account", authentication: true);
            var login = ParseObject(loginResponse.Content, "authentication response");
            if (!ReadBoolean(login, "active"))
            {
                throw new ZattooAuthenticationException("Zattoo rejected the account authentication.");
            }

            return login;
        }

        private async Task<string> LoadAppTokenAsync(CancellationToken cancellationToken)
        {
            var directResponse = await transport.GetAsync(AppTokenPath, cancellationToken).ConfigureAwait(false);
            if (directResponse.IsSuccessStatusCode
                && TryReadAppToken(directResponse.Content, out var directToken))
            {
                return directToken;
            }

            var loginPageResponse = await transport.GetAsync(LoginPagePath, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(loginPageResponse, "loading Zattoo application metadata", authentication: false);

            var inlineMatch = InlineAppTokenRegex.Match(loginPageResponse.Content);
            if (inlineMatch.Success && !string.IsNullOrWhiteSpace(inlineMatch.Groups["value"].Value))
            {
                return inlineMatch.Groups["value"].Value;
            }

            var scriptMatch = AppScriptRegex.Match(loginPageResponse.Content);
            if (!scriptMatch.Success)
            {
                throw new ZattooProtocolException("Unable to locate Zattoo application token metadata.");
            }

            var scriptPath = NormalizeRelativePath(scriptMatch.Groups["value"].Value);
            var scriptResponse = await transport.GetAsync(scriptPath, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(scriptResponse, "loading Zattoo application metadata", authentication: false);

            var tokenJsonMatch = TokenJsonRegex.Match(scriptResponse.Content);
            if (!tokenJsonMatch.Success)
            {
                throw new ZattooProtocolException("Unable to locate the Zattoo application token document.");
            }

            var tokenPath = NormalizeRelativePath(tokenJsonMatch.Groups["value"].Value);
            var tokenResponse = await transport.GetAsync(tokenPath, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(tokenResponse, "loading Zattoo application metadata", authentication: false);

            if (!TryReadAppToken(tokenResponse.Content, out var discoveredToken))
            {
                throw new ZattooProtocolException("The Zattoo application token document is invalid.");
            }

            return discoveredToken;
        }

        private async Task<ZattooTransportResponse> SendAuthenticatedWithRetryAsync(
            Func<CancellationToken, Task<ZattooTransportResponse>> request,
            string operation,
            CancellationToken cancellationToken)
        {
            var response = await request(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized
                && response.StatusCode != HttpStatusCode.Forbidden)
            {
                EnsureSuccess(response, operation, authentication: false);
                return response;
            }

            Invalidate();
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

            response = await request(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
            {
                Invalidate();
                var message = response.StatusCode == HttpStatusCode.Forbidden
                    ? "Zattoo refused the request after one session renewal."
                    : "The Zattoo session remained unauthorized after one renewal.";
                throw new ZattooSessionExpiredException(
                    message,
                    response.StatusCode);
            }

            EnsureSuccess(response, operation, authentication: false);
            return response;
        }

        private async Task<ZattooChannel> GetChannelAsync(
            string channelId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentException("A Zattoo channel ID is required.", nameof(channelId));
            }

            lock (stateLock)
            {
                if (channelsById.TryGetValue(channelId, out var cached))
                {
                    return cached;
                }
            }

            var channels = await GetChannelsAsync(cancellationToken).ConfigureAwait(false);
            var channel = channels.FirstOrDefault(
                item => string.Equals(item.Id, channelId, StringComparison.Ordinal));
            if (channel == null)
            {
                throw new ZattooStreamUnavailableException(
                    "The selected Zattoo channel does not exist in the current catalogue.");
            }

            return channel;
        }

        private async Task<ZattooStream> RequestStreamAsync(
            string channelId,
            ZattooQuality quality,
            ZattooStreamFormat format,
            CancellationToken cancellationToken)
        {
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

            var streamType = format switch
            {
                ZattooStreamFormat.Dash => "dash",
                ZattooStreamFormat.Hls => "hls7",
                ZattooStreamFormat.MpegTs => "hls",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(format),
                    format,
                    "Only non-DRM DASH, HLS7 and HLS MPEG-TS streams are supported."),
            };

            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cid"] = channelId,
                ["quality"] = quality.Level,
                ["stream_type"] = streamType,
                ["https_watch_urls"] = "true",
                ["format"] = "json",
            };
            const string relativePath = "/zapi/watch";
            var response = await SendAuthenticatedWithRetryAsync(
                token => transport.PostFormAsync(relativePath, fields, token),
                "opening a non-DRM live stream",
                cancellationToken).ConfigureAwait(false);

            return ParseStream(response.Content, quality, format);
        }

        private static ZattooStream ParseStream(
            string content,
            ZattooQuality quality,
            ZattooStreamFormat format)
        {
            var result = CreateStream(quality, format);
            var root = ParseObject(content, "live stream response");
            if ((root.TryGetProperty("success", out _)
                    && !ReadBoolean(root, "success"))
                || !root.TryGetProperty("stream", out var stream)
                || stream.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            var url = ReadString(stream, "url");
            var responseDrm = ReadBoolean(root, "drm_required")
                || ReadBoolean(stream, "drm_required");
            var width = ReadNullableInt32(stream, "width") ?? quality.Width;
            var height = ReadNullableInt32(stream, "height") ?? quality.Height;
            var bitrate = ReadNullableInt32(stream, "maxrate")
                ?? ReadNullableInt32(stream, "bitrate")
                ?? quality.BitrateKbps;

            if (stream.TryGetProperty("watch_urls", out var watchUrls)
                && watchUrls.ValueKind == JsonValueKind.Array)
            {
                foreach (var watchUrl in watchUrls.EnumerateArray())
                {
                    if (watchUrl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(ReadString(watchUrl, "license_url")))
                    {
                        responseDrm = true;
                    }

                    var candidateUrl = ReadString(watchUrl, "url");
                    if (!string.IsNullOrWhiteSpace(candidateUrl))
                    {
                        url = candidateUrl;
                        bitrate = ReadNullableInt32(watchUrl, "maxrate") ?? bitrate;
                        width = ReadNullableInt32(watchUrl, "width") ?? width;
                        height = ReadNullableInt32(watchUrl, "height") ?? height;
                        break;
                    }
                }
            }

            result.DrmRequired = result.DrmRequired || responseDrm;
            result.Width = width;
            result.Height = height;
            result.BitrateKbps = bitrate;
            if (result.DrmRequired || string.IsNullOrWhiteSpace(url))
            {
                return result;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var streamUri)
                || streamUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ZattooProtocolException(
                    "Zattoo returned an invalid or insecure live stream URL.");
            }

            result.Url = streamUri.AbsoluteUri;
            return result;
        }

        private static ZattooStream CreateUnsupportedDrmStream(ZattooQuality quality)
        {
            var stream = CreateStream(quality, ZattooStreamFormat.Dash);
            stream.DrmRequired = true;
            return stream;
        }

        private static ZattooStream CreateStream(
            ZattooQuality quality,
            ZattooStreamFormat format)
        {
            return new ZattooStream
            {
                Format = format,
                Quality = quality.Label,
                Width = quality.Width,
                Height = quality.Height,
                BitrateKbps = quality.BitrateKbps,
                DrmRequired = quality.DrmRequired,
            };
        }

        private string BuildChannelsPath()
        {
            var info = SessionInfo;
            if (info == null || string.IsNullOrWhiteSpace(info.PowerGuideHash))
            {
                throw new ZattooSessionExpiredException("No active Zattoo guide session is available.");
            }

            return "/zapi/v3/cached/"
                + Uri.EscapeDataString(info.PowerGuideHash)
                + "/channels";
        }

        private async Task<string> LoadGuideWindowContentAsync(
            long windowStart,
            long windowEnd,
            CancellationToken cancellationToken)
        {
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            var response = await SendAuthenticatedWithRetryAsync(
                token => transport.GetAsync(
                    BuildGuidePath(windowStart, windowEnd),
                    token),
                "loading guide data",
                cancellationToken).ConfigureAwait(false);
            return response.Content;
        }

        private string BuildGuidePath(long startTime, long endTime)
        {
            var info = SessionInfo;
            if (info == null || string.IsNullOrWhiteSpace(info.PowerGuideHash))
            {
                throw new ZattooSessionExpiredException(
                    "No active Zattoo guide session is available.");
            }

            return "/zapi/v3/cached/"
                + Uri.EscapeDataString(info.PowerGuideHash)
                + "/guide?end="
                + endTime.ToString(CultureInfo.InvariantCulture)
                + "&start="
                + startTime.ToString(CultureInfo.InvariantCulture)
                + "&format=json";
        }

        private string BuildLegacyGuidePath(long startTime, long endTime)
        {
            var info = SessionInfo;
            if (info == null || string.IsNullOrWhiteSpace(info.PowerGuideHash))
            {
                throw new ZattooSessionExpiredException(
                    "No active Zattoo guide session is available.");
            }

            return "/zapi/v2/cached/program/power_guide/"
                + Uri.EscapeDataString(info.PowerGuideHash)
                + "?end="
                + endTime.ToString(CultureInfo.InvariantCulture)
                + "&start="
                + startTime.ToString(CultureInfo.InvariantCulture);
        }

        private async Task<GuideEndpointSurveyDocument> LoadGuideEndpointForSurveyAsync(
            string path,
            string operation,
            CancellationToken cancellationToken)
        {
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            var response = await SendAuthenticatedWithRetryAsync(
                    token => transport.GetAsync(path, token),
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            return new GuideEndpointSurveyDocument(
                response.Content,
                stopwatch.Elapsed);
        }

        private static ZattooGuideEndpointComparison CreateGuideEndpointComparison(
            DateTimeOffset start,
            DateTimeOffset end,
            GuideEndpointSurveyDocument version2,
            GuideEndpointSurveyDocument version3)
        {
            var version2Programs = ZattooGuideService.ParseProgramsForSurvey(
                version2.Content);
            var version3Programs = ZattooGuideService.ParseProgramsForSurvey(
                version3.Content);
            var version2ByIdentity = version2Programs
                .GroupBy(CreateProgramIdentity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var version3ByIdentity = version3Programs
                .GroupBy(CreateProgramIdentity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var sharedIdentities = version2ByIdentity.Keys
                .Intersect(version3ByIdentity.Keys, StringComparer.Ordinal)
                .ToArray();

            return new ZattooGuideEndpointComparison
            {
                StartDate = start,
                EndDate = end,
                Version2 = CreateGuideEndpointMetrics(version2, version2Programs),
                Version3 = CreateGuideEndpointMetrics(version3, version3Programs),
                SharedPrograms = sharedIdentities.Length,
                Version2OnlyPrograms = version2ByIdentity.Count - sharedIdentities.Length,
                Version3OnlyPrograms = version3ByIdentity.Count - sharedIdentities.Length,
                SharedDescriptionsOnlyInVersion2 = sharedIdentities.Count(identity =>
                    !string.IsNullOrWhiteSpace(version2ByIdentity[identity].Overview)
                    && string.IsNullOrWhiteSpace(version3ByIdentity[identity].Overview)),
                SharedDescriptionsOnlyInVersion3 = sharedIdentities.Count(identity =>
                    string.IsNullOrWhiteSpace(version2ByIdentity[identity].Overview)
                    && !string.IsNullOrWhiteSpace(version3ByIdentity[identity].Overview)),
            };
        }

        private static ZattooGuideEndpointMetrics CreateGuideEndpointMetrics(
            GuideEndpointSurveyDocument document,
            IReadOnlyCollection<ZattooProgram> programs)
        {
            return new ZattooGuideEndpointMetrics
            {
                ResponseBytes = Encoding.UTF8.GetByteCount(document.Content),
                Elapsed = document.Elapsed,
                ChannelsWithPrograms = programs
                    .Select(program => program.ChannelId)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                Programs = programs.Count,
                ProgramsWithDescription = programs.Count(program =>
                    !string.IsNullOrWhiteSpace(program.Overview)),
                ProgramsWithEpisodeTitle = programs.Count(program =>
                    !string.IsNullOrWhiteSpace(program.EpisodeTitle)),
                ProgramsWithGenres = programs.Count(program => program.Genres.Count > 0),
                ProgramsWithSeasonOrEpisodeNumber = programs.Count(program =>
                    program.SeasonNumber.HasValue || program.EpisodeNumber.HasValue),
                ProgramsWithImage = programs.Count(program =>
                    !string.IsNullOrWhiteSpace(program.ImageUrl)),
            };
        }

        private static string CreateProgramIdentity(ZattooProgram program)
        {
            return program.ChannelId
                + "\n"
                + program.Id
                + "\n"
                + program.StartDate.UtcDateTime.Ticks.ToString(
                    CultureInfo.InvariantCulture);
        }

        private string BuildProgramDetailsPath(IReadOnlyList<string> programIds)
        {
            var info = SessionInfo;
            if (info == null || string.IsNullOrWhiteSpace(info.PowerGuideHash))
            {
                throw new ZattooSessionExpiredException(
                    "No active Zattoo guide session is available.");
            }

            return "/zapi/v2/cached/program/power_details/"
                + Uri.EscapeDataString(info.PowerGuideHash)
                + "?complete=True&program_ids="
                + string.Join(",", programIds.Select(Uri.EscapeDataString));
        }

        private static IReadOnlyList<ZattooProgramDetails> ParseProgramDetails(
            string content)
        {
            var root = ParseObject(content, "program details response");
            if (!ReadBoolean(root, "success")
                || !root.TryGetProperty("programs", out var programs)
                || programs.ValueKind != JsonValueKind.Array)
            {
                throw new ZattooProtocolException(
                    "The Zattoo program details response is invalid.");
            }

            var result = new List<ZattooProgramDetails>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in programs.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadIdentifier(element, "id");
                if (id.Length == 0 || !seen.Add(id))
                {
                    continue;
                }

                result.Add(new ZattooProgramDetails
                {
                    Id = id,
                    EpisodeTitle = EmptyToNull(ReadString(element, "et")),
                    Overview = EmptyToNull(ReadString(element, "d")),
                    Genres = ReadStringArray(element, "g"),
                    SeasonNumber = ReadNonNegativeInt32(element, "s_no"),
                    EpisodeNumber = ReadNonNegativeInt32(element, "e_no"),
                });
            }

            return result;
        }

        /// <summary>
        /// Loads the favorite channel IDs, or returns null when the provider does
        /// not deliver them. Favorites decorate the catalogue and must never
        /// prevent it from loading.
        /// </summary>
        private async Task<HashSet<string>?> LoadFavoritesAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await SendAuthenticatedWithRetryAsync(
                    token => transport.GetAsync(FavoritesPath, token),
                    "loading channel favorites",
                    cancellationToken).ConfigureAwait(false);
                return ParseFavorites(response.Content);
            }
            catch (ZattooAuthenticationException)
            {
                // Bad credentials must surface instead of triggering a second
                // login attempt from the catalogue request.
                throw;
            }
            catch (ZattooException)
            {
                return null;
            }
        }

        private static HashSet<string> ParseFavorites(string content)
        {
            var root = ParseObject(content, "favorites response");
            if (!ReadBoolean(root, "success")
                || !root.TryGetProperty("favorites", out var favoritesElement)
                || favoritesElement.ValueKind != JsonValueKind.Array)
            {
                throw new ZattooProtocolException("The Zattoo favorites response is invalid.");
            }

            var favorites = new HashSet<string>(StringComparer.Ordinal);
            foreach (var favorite in favoritesElement.EnumerateArray())
            {
                if (favorite.ValueKind == JsonValueKind.String)
                {
                    var id = favorite.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        favorites.Add(id!);
                    }
                }
            }

            return favorites;
        }

        private static IReadOnlyList<ZattooChannel> ParseChannels(
            string content,
            HashSet<string> favorites)
        {
            var root = ParseObject(content, "channels response");
            if (!root.TryGetProperty("channels", out var channelsElement)
                || channelsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ZattooProtocolException("The Zattoo channels response is invalid.");
            }

            var result = new List<ZattooChannel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var channelElement in channelsElement.EnumerateArray())
            {
                if (channelElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadString(channelElement, "cid");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                ReadChannelPresentation(channelElement, out var name, out var logoPath);

                // The provider publishes its own channel number. Falling back to
                // the catalogue position would renumber every later channel as
                // soon as one is added or removed.
                var number = ReadNullableInt32(channelElement, "number");
                result.Add(new ZattooChannel
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    Number = number > 0 ? number.Value : result.Count + 1,
                    LogoUrl = BuildLogoUrl(logoPath),
                    IsFavorite = favorites.Contains(id),
                    Qualities = ParseQualities(channelElement),
                });
            }

            return result;
        }

        private static IReadOnlyList<ZattooQuality> ParseQualities(JsonElement channel)
        {
            var result = new List<ZattooQuality>();
            if (!channel.TryGetProperty("qualities", out var qualities)
                || qualities.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var quality in qualities.EnumerateArray())
            {
                if (quality.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var level = ReadString(quality, "level");
                var height = ReadNullableInt32(quality, "height")
                    ?? ZattooQualitySelector.InferHeight(level);
                result.Add(new ZattooQuality
                {
                    Level = level,
                    Label = ZattooQualitySelector.CreateLabel(level, height),
                    Width = ReadNullableInt32(quality, "width"),
                    Height = height,
                    BitrateKbps = ReadNullableInt32(quality, "maxrate")
                        ?? ReadNullableInt32(quality, "bitrate"),
                    IsAvailable = string.Equals(
                        ReadString(quality, "availability"),
                        "available",
                        StringComparison.OrdinalIgnoreCase),
                    DrmRequired = ReadBoolean(quality, "drm_required"),
                });
            }

            return result;
        }

        private static void ReadChannelPresentation(
            JsonElement channel,
            out string name,
            out string logoPath)
        {
            name = string.Empty;
            logoPath = string.Empty;
            if (!channel.TryGetProperty("qualities", out var qualities)
                || qualities.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            JsonElement? fallback = null;
            foreach (var quality in qualities.EnumerateArray())
            {
                if (quality.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (fallback == null)
                {
                    fallback = quality.Clone();
                }

                if (string.Equals(ReadString(quality, "availability"), "available", StringComparison.OrdinalIgnoreCase))
                {
                    name = ReadString(quality, "title");
                    logoPath = ReadString(quality, "logo_white_84");
                    return;
                }
            }

            if (fallback.HasValue)
            {
                name = ReadString(fallback.Value, "title");
                logoPath = ReadString(fallback.Value, "logo_white_84");
            }
        }

        private static string? BuildLogoUrl(string logoPath)
        {
            if (string.IsNullOrWhiteSpace(logoPath))
            {
                return null;
            }

            var normalized = logoPath.Trim();
            if (normalized.IndexOf('\\') >= 0)
            {
                return null;
            }

            if (normalized.StartsWith("//", StringComparison.Ordinal))
            {
                return Uri.TryCreate("https:" + normalized, UriKind.Absolute, out var networkPath)
                    && networkPath.Scheme == Uri.UriSchemeHttps
                    ? networkPath.AbsoluteUri
                    : null;
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

                // A rooted web path can be parsed as file:// on Unix. It is
                // still relative to the logo host and must remain supported.
                if (!normalized.StartsWith("/", StringComparison.Ordinal))
                {
                    return null;
                }
            }

            return new Uri(new Uri("https://logos.zattic.com/"), normalized).AbsoluteUri;
        }

        private static bool TryReadAppToken(string content, out string token)
        {
            token = string.Empty;
            try
            {
                var root = ParseObject(content, "application token response");
                if (!ReadBoolean(root, "success"))
                {
                    return false;
                }

                token = ReadString(root, "session_token");
                return !string.IsNullOrWhiteSpace(token);
            }
            catch (ZattooProtocolException)
            {
                return false;
            }
        }

        private static JsonElement ParseObject(string content, string operation)
        {
            try
            {
                using (var document = JsonDocument.Parse(content))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new ZattooProtocolException("Zattoo returned an invalid " + operation + ".");
                    }

                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                throw new ZattooProtocolException("Zattoo returned malformed JSON for " + operation + ".");
            }
        }

        private static string ReadString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return property.GetString() ?? string.Empty;
        }

        private static bool ReadBoolean(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            return property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out var numeric)
                && numeric != 0;
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

        private static int? ReadNonNegativeInt32(
            JsonElement element,
            string propertyName)
        {
            var value = ReadNullableInt32(element, propertyName);
            return value >= 0 ? value : null;
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

        private static bool HasAccount(JsonElement element)
        {
            return element.TryGetProperty("account", out var account)
                && account.ValueKind == JsonValueKind.Object;
        }

        private static void EnsureSuccess(
            ZattooTransportResponse response,
            string operation,
            bool authentication)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            if (response.StatusCode == (HttpStatusCode)429)
            {
                throw new ZattooRateLimitException(
                    "Zattoo rate-limited the request while " + operation + ".",
                    response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
            {
                if (authentication)
                {
                    throw new ZattooAuthenticationException(
                        "Zattoo refused the request while " + operation + ".",
                        response.StatusCode);
                }

                throw new ZattooSessionExpiredException(
                    "The Zattoo session is unauthorized while " + operation + ".",
                    response.StatusCode);
            }

            throw new ZattooApiException(
                "Zattoo returned HTTP " + (int)response.StatusCode + " while " + operation + ".",
                response.StatusCode);
        }

        private static string NormalizeRelativePath(string path)
        {
            var normalized = WebUtility.HtmlDecode(path).Trim();
            if (normalized.Length == 0
                || normalized.StartsWith("//", StringComparison.Ordinal)
                || normalized.IndexOf('\\') >= 0
                || normalized.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                throw new ZattooProtocolException("Zattoo application metadata referenced an external resource.");
            }

            if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                return normalized;
            }

            if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
            {
                throw new ZattooProtocolException("Zattoo application metadata referenced an external resource.");
            }

            return "/" + normalized;
        }

        private static ZattooSessionInfo CopySessionInfo(ZattooSessionInfo source)
        {
            return new ZattooSessionInfo
            {
                IsActive = source.IsActive,
                CreatedAt = source.CreatedAt,
                CountryCode = source.CountryCode,
                ServiceCountry = source.ServiceCountry,
                ReplayAvailable = source.ReplayAvailable,
                RecordingNumberLimit = source.RecordingNumberLimit,
                MaximumConcurrentStreams = source.MaximumConcurrentStreams,
                ConcurrentStreamLimitIsInferred =
                    source.ConcurrentStreamLimitIsInferred,
                FavoritesAvailable = source.FavoritesAvailable,
                PlayableChannelCount = source.PlayableChannelCount,
                DrmOnlyChannelCount = source.DrmOnlyChannelCount,
                UnavailableChannelCount = source.UnavailableChannelCount,
                MaximumPlayableHeight = source.MaximumPlayableHeight,
                PowerGuideHash = source.PowerGuideHash,
            };
        }

        private static int? ReadConcurrentStreamLimit(
            JsonElement session,
            JsonElement account,
            JsonElement nonlive)
        {
            var candidates = new[] { session, account, nonlive };
            var names = new[]
            {
                "max_concurrent_streams",
                "concurrent_stream_limit",
                "streaming_number_limit",
            };
            foreach (var candidate in candidates)
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var name in names)
                {
                    var value = ReadNullableInt32(candidate, name);
                    if (value > 0)
                    {
                        return Math.Min(4, value.Value);
                    }
                }
            }

            return null;
        }

        private static int InferConcurrentStreamLimit(int recordingNumberLimit)
        {
            if (recordingNumberLimit >= 2000)
            {
                return 4;
            }

            return recordingNumberLimit > 0 ? 2 : 1;
        }

        private sealed class GuideEndpointSurveyDocument
        {
            public GuideEndpointSurveyDocument(string content, TimeSpan elapsed)
            {
                Content = content;
                Elapsed = elapsed;
            }

            public string Content { get; }

            public TimeSpan Elapsed { get; }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ZattooClient));
            }
        }
    }
}
