using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
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

            var favoritesResponse = await SendAuthenticatedWithRetryAsync(
                token => transport.GetAsync(FavoritesPath, token),
                "loading channel favorites",
                cancellationToken).ConfigureAwait(false);
            var favorites = ParseFavorites(favoritesResponse.Content);

            var channelsResponse = await SendAuthenticatedWithRetryAsync(
                token => transport.GetAsync(BuildChannelsPath(), token),
                "loading channels",
                cancellationToken).ConfigureAwait(false);

            var channels = ParseChannels(channelsResponse.Content, favorites);
            lock (stateLock)
            {
                channelsById.Clear();
                foreach (var channel in channels)
                {
                    channelsById[channel.Id] = channel;
                }
            }

            return channels;
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
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Invalidate();
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
            var newSession = new ZattooSessionInfo
            {
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                CountryCode = ReadString(session, "current_country"),
                ServiceCountry = ReadString(account, "service_country"),
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
                result.Add(new ZattooChannel
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    Number = result.Count + 1,
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

            if (logoPath.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + logoPath;
            }

            if (Uri.TryCreate(logoPath, UriKind.Absolute, out var absolute))
            {
                return absolute.Scheme == Uri.UriSchemeHttp
                    ? "https://" + absolute.Host + absolute.PathAndQuery
                    : absolute.AbsoluteUri;
            }

            return new Uri(new Uri("https://logos.zattic.com/"), logoPath).AbsoluteUri;
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
            if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
            {
                throw new ZattooProtocolException("Zattoo application metadata referenced an external resource.");
            }

            return normalized.StartsWith("/", StringComparison.Ordinal)
                ? normalized
                : "/" + normalized;
        }

        private static ZattooSessionInfo CopySessionInfo(ZattooSessionInfo source)
        {
            return new ZattooSessionInfo
            {
                IsActive = source.IsActive,
                CreatedAt = source.CreatedAt,
                CountryCode = source.CountryCode,
                ServiceCountry = source.ServiceCountry,
                PowerGuideHash = source.PowerGuideHash,
            };
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
