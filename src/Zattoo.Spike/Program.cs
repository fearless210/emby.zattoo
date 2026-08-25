using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Infrastructure;
using Emby.Zattoo.Models;
using Emby.Zattoo.Zattoo;

if (args.Length > 0
    && (string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase)
        || string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase)
        || string.Equals(args[0], "-h", StringComparison.OrdinalIgnoreCase)))
{
    PrintUsage();
    return 0;
}

var username = Environment.GetEnvironmentVariable("ZATTOO_USERNAME");
var password = Environment.GetEnvironmentVariable("ZATTOO_PASSWORD");

if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine(
        "Set ZATTOO_USERNAME and ZATTOO_PASSWORD before running this command.");
    return 2;
}

var options = new ZattooClientOptions
{
    Username = username,
    Password = password,
};

var provider = Environment.GetEnvironmentVariable("ZATTOO_PROVIDER_URL");
if (!string.IsNullOrWhiteSpace(provider))
{
    if (!Uri.TryCreate(provider, UriKind.Absolute, out var providerUri)
        || providerUri.Scheme != Uri.UriSchemeHttps)
    {
        Console.Error.WriteLine("ZATTOO_PROVIDER_URL must be an absolute HTTPS URL.");
        return 2;
    }

    options.ProviderBaseUri = providerUri;
}

var applicationVersion = Environment.GetEnvironmentVariable("ZATTOO_APP_VERSION");
if (!string.IsNullOrWhiteSpace(applicationVersion))
{
    options.ApplicationVersion = applicationVersion;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    using IZattooClient client = new ZattooClient(options);
    await client.LoginAsync(cancellation.Token);

    var command = args.Length == 0 ? "channels" : args[0].ToLowerInvariant();
    return command switch
    {
        "channels" => await PrintChannelsAsync(client, cancellation.Token),
        "survey" => await PrintSurveyAsync(client, cancellation.Token),
        "streams" => await PrintStreamsAsync(client, args, cancellation.Token),
        "probe" => await ProbeAsync(client, args, cancellation.Token),
        "ffmpeg-test" => await FfmpegTestAsync(client, args, cancellation.Token),
        _ => PrintUnknownCommand(),
    };
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (ZattooApiException exception)
{
    var status = exception.StatusCode.HasValue
        ? $" (HTTP {(int)exception.StatusCode.Value})"
        : string.Empty;
    Console.Error.WriteLine(
        SensitiveDataSanitizer.SanitizeText(exception.Message) + status);
    return 1;
}
catch (ZattooException exception)
{
    Console.Error.WriteLine(SensitiveDataSanitizer.SanitizeText(exception.Message));
    return 1;
}
catch (Exception)
{
    Console.Error.WriteLine("Unexpected failure while running the Zattoo spike.");
    return 1;
}

static async Task<int> PrintChannelsAsync(
    IZattooClient client,
    CancellationToken cancellationToken)
{
    var channels = await client.GetChannelsAsync(cancellationToken);
    Console.WriteLine("Authentication: OK");
    Console.WriteLine("Session: active");
    Console.WriteLine();
    Console.WriteLine($"{channels.Count} channels found");
    Console.WriteLine();

    foreach (var channel in channels)
    {
        var favorite = channel.IsFavorite ? " *" : string.Empty;
        Console.WriteLine($"{channel.Number:D3} | {channel.Name} | {channel.Id}{favorite}");
    }

    return 0;
}

static async Task<int> PrintSurveyAsync(
    IZattooClient client,
    CancellationToken cancellationToken)
{
    var channels = await client.GetChannelsAsync(cancellationToken);
    var statistics = ZattooStreamStatistics.Calculate(channels);

    Console.WriteLine("Catalogue stream survey (no playback URL requested)");
    Console.WriteLine();
    Console.WriteLine($"Total chaînes        : {statistics.TotalChannels}");
    Console.WriteLine($"Streams disponibles  : {statistics.ChannelsWithAvailableStreams}");
    Console.WriteLine($"Non-DRM              : {statistics.ChannelsWithNonDrmStreams}");
    Console.WriteLine($"DRM uniquement       : {statistics.DrmOnlyChannels}");
    Console.WriteLine($"Sans stream disponible: {statistics.ChannelsWithoutAvailableStreams}");
    Console.WriteLine();
    Console.WriteLine("GO / NO-GO remains pending until a non-DRM stream passes probe and ffmpeg-test.");
    return 0;
}

static async Task<int> PrintStreamsAsync(
    IZattooClient client,
    string[] arguments,
    CancellationToken cancellationToken)
{
    if (arguments.Length < 2)
    {
        Console.Error.WriteLine("Usage: streams <channel-id|number|exact-name>");
        return 2;
    }

    var channels = await client.GetChannelsAsync(cancellationToken);
    var channel = ResolveChannel(channels, arguments[1]);
    var streams = await client.GetStreamOptionsAsync(channel.Id, cancellationToken);

    Console.WriteLine(channel.Name);
    Console.WriteLine();
    if (streams.Count == 0)
    {
        Console.WriteLine("No stream option advertised.");
        return 1;
    }

    foreach (var stream in streams)
    {
        var drm = stream.DrmRequired ? "yes" : "no";
        var status = stream.DrmRequired
            ? "Unsupported"
            : stream.IsSupported ? "Usable" : "Unavailable";
        Console.WriteLine(
            $"{stream.Quality,-8} {FormatName(stream.Format),-6} DRM: {drm,-3} {status}");
    }

    if (!streams.Any(stream => stream.IsSupported))
    {
        Console.WriteLine();
        Console.WriteLine("No usable non-DRM stream.");
        return 1;
    }

    return 0;
}

static async Task<int> ProbeAsync(
    IZattooClient client,
    string[] arguments,
    CancellationToken cancellationToken)
{
    if (arguments.Length < 2)
    {
        Console.Error.WriteLine(
            "Usage: probe <channel-id|number|exact-name> [auto|1080p|720p|540p] [dash|hls|hls-ts]");
        return 2;
    }

    var preference = ParsePreference(arguments.Length >= 3 ? arguments[2] : "auto");
    var format = ParseStreamFormat(arguments.Length >= 4 ? arguments[3] : "dash");
    var channels = await client.GetChannelsAsync(cancellationToken);
    var channel = ResolveChannel(channels, arguments[1]);
    var stream = await client.GetStreamAsync(
        channel.Id,
        preference,
        format,
        cancellationToken);

    Console.WriteLine($"Probing {channel.Name}: {stream.Quality} {FormatName(stream.Format)} non-DRM");
    var executable = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe";
    var result = await MediaToolRunner.RunFfprobeAsync(
        executable,
        stream.Url!,
        cancellationToken);
    PrintMediaToolResult("ffprobe", "FFPROBE_PATH", result);
    return result.ExitCode == 0 ? 0 : 1;
}

static async Task<int> FfmpegTestAsync(
    IZattooClient client,
    string[] arguments,
    CancellationToken cancellationToken)
{
    if (arguments.Length < 2)
    {
        Console.Error.WriteLine(
            "Usage: ffmpeg-test <channel-id|number|exact-name> [seconds] [auto|1080p|720p|540p] [dash|hls|hls-ts]");
        return 2;
    }

    var durationSeconds = 30;
    if (arguments.Length >= 3
        && (!int.TryParse(arguments[2], out durationSeconds)
            || durationSeconds < 5
            || durationSeconds > 1800))
    {
        Console.Error.WriteLine("Duration must be between 5 and 1800 seconds.");
        return 2;
    }

    var preference = ParsePreference(arguments.Length >= 4 ? arguments[3] : "auto");
    var format = ParseStreamFormat(arguments.Length >= 5 ? arguments[4] : "dash");
    var channels = await client.GetChannelsAsync(cancellationToken);
    var channel = ResolveChannel(channels, arguments[1]);
    var stream = await client.GetStreamAsync(
        channel.Id,
        preference,
        format,
        cancellationToken);

    Console.WriteLine(
        $"Testing {channel.Name}: {stream.Quality} {FormatName(stream.Format)} non-DRM -> MPEG-TS for {durationSeconds}s wall-clock");
    IReadOnlyList<string> mediaInputs = new[] { stream.Url! };
    if (stream.Format == ZattooStreamFormat.Hls)
    {
        var selection = await HlsManifestResolver.ResolveAsync(
            stream.Url!,
            stream.Height,
            cancellationToken);
        mediaInputs = selection.AudioUri == null
            ? new[] { selection.VideoUri.AbsoluteUri }
            : new[] { selection.VideoUri.AbsoluteUri, selection.AudioUri.AbsoluteUri };
        Console.WriteLine(
            selection.IsMasterPlaylist
                ? $"HLS master resolved: one video rendition and {(selection.AudioUri == null ? "muxed/optional" : "one default")} audio rendition"
                : "HLS media playlist used directly");
    }

    var executable = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
    var result = await MediaToolRunner.RunFfmpegCopyTestAsync(
        executable,
        mediaInputs,
        TimeSpan.FromSeconds(durationSeconds),
        discardAlternateStreams: stream.Format == ZattooStreamFormat.Dash,
        cancellationToken: cancellationToken);
    PrintMediaToolResult("ffmpeg", "FFMPEG_PATH", result);
    var minimumMediaDuration = TimeSpan.FromSeconds(durationSeconds * 0.8);
    var passed = result.ExitCode == 0
        && result.MediaProcessed >= minimumMediaDuration
        && result.FragmentFailureCount == 0
        && result.CorruptionWarningCount == 0;
    Console.WriteLine($"Assessment: {(passed ? "PASS" : "FAIL")}");
    return passed ? 0 : 1;
}

static ZattooChannel ResolveChannel(
    IReadOnlyList<ZattooChannel> channels,
    string selector)
{
    var byId = channels.FirstOrDefault(
        channel => string.Equals(channel.Id, selector, StringComparison.Ordinal));
    if (byId != null)
    {
        return byId;
    }

    if (int.TryParse(selector, out var number))
    {
        var byNumber = channels.FirstOrDefault(channel => channel.Number == number);
        if (byNumber != null)
        {
            return byNumber;
        }
    }

    var byName = channels.FirstOrDefault(
        channel => string.Equals(channel.Name, selector, StringComparison.OrdinalIgnoreCase));
    return byName
        ?? throw new ZattooStreamUnavailableException(
            "No channel matched the supplied selector.");
}

static ZattooPreferredQuality ParsePreference(string value)
{
    return value.ToLowerInvariant() switch
    {
        "auto" => ZattooPreferredQuality.Auto,
        "1080" or "1080p" => ZattooPreferredQuality.P1080,
        "720" or "720p" => ZattooPreferredQuality.P720,
        "540" or "540p" => ZattooPreferredQuality.P540,
        _ => throw new ZattooStreamUnavailableException(
            "Quality must be auto, 1080p, 720p or 540p."),
    };
}

static ZattooStreamFormat ParseStreamFormat(string value)
{
    return value.ToLowerInvariant() switch
    {
        "dash" => ZattooStreamFormat.Dash,
        "hls" or "hls7" => ZattooStreamFormat.Hls,
        "hls-ts" or "hls-mpegts" => ZattooStreamFormat.MpegTs,
        _ => throw new ZattooStreamUnavailableException(
            "Stream format must be dash, hls or hls-ts."),
    };
}

static string FormatName(ZattooStreamFormat format)
{
    return format switch
    {
        ZattooStreamFormat.Dash => "DASH",
        ZattooStreamFormat.Hls => "HLS",
        ZattooStreamFormat.MpegTs => "MPEG-TS",
        _ => "unknown",
    };
}

static void PrintMediaToolResult(
    string toolName,
    string pathVariable,
    MediaToolResult result)
{
    if (!result.ToolAvailable)
    {
        Console.Error.WriteLine(
            $"{toolName} was not found. Install it or set {pathVariable}.");
        return;
    }

    Console.WriteLine($"Exit code: {result.ExitCode}");
    Console.WriteLine($"Elapsed: {result.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"Process CPU: {result.CpuTime.TotalSeconds:F1}s");
    if (result.MediaProcessed.HasValue)
    {
        Console.WriteLine($"Media processed: {result.MediaProcessed.Value.TotalSeconds:F1}s");
        Console.WriteLine($"Progress reports: {result.ProgressReportCount}");
        Console.WriteLine($"Last reported speed: {result.LastReportedSpeed}");
        Console.WriteLine($"Fragment failures: {result.FragmentFailureCount}");
        Console.WriteLine($"Corruption warnings: {result.CorruptionWarningCount}");
        Console.WriteLine($"Duplicated MOOV warnings: {result.DuplicateMoovWarningCount}");
    }
    if (result.TimedOut)
    {
        Console.WriteLine("Timed out: yes");
    }

    if (!string.IsNullOrWhiteSpace(result.StandardOutput))
    {
        Console.WriteLine();
        Console.WriteLine(SensitiveDataSanitizer.SanitizeText(result.StandardOutput));
    }

    if (!string.IsNullOrWhiteSpace(result.StandardError))
    {
        Console.Error.WriteLine(SensitiveDataSanitizer.SanitizeText(result.StandardError));
    }
}

static int PrintUnknownCommand()
{
    Console.Error.WriteLine("Unknown command.");
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("Zattoo.Spike commands:");
    Console.WriteLine("  channels");
    Console.WriteLine("  survey");
    Console.WriteLine("  streams <channel-id|number|exact-name>");
    Console.WriteLine("  probe <channel-id|number|exact-name> [auto|1080p|720p|540p] [dash|hls|hls-ts]");
    Console.WriteLine("  ffmpeg-test <channel-id|number|exact-name> [seconds] [auto|1080p|720p|540p] [dash|hls|hls-ts]");
}
