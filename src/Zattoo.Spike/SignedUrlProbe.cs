using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

/// <summary>
/// Requests one signed URL and reports only what an observation may reveal: the
/// HTTP status, and the response body when it is a playlist. The URL itself is
/// never returned, so a caller cannot print it by accident.
/// </summary>
internal static class SignedUrlProbe
{
    private const int MaximumRedirects = 3;

    private static readonly HttpClient Client = CreateClient();

    public static async Task<SignedUrlProbeResult> ProbeAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            var currentUri = uri;
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                using var response = await Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (redirect == MaximumRedirects || location == null)
                    {
                        return SignedUrlProbeResult.Failed("too many redirects");
                    }

                    if (!Uri.TryCreate(currentUri, location, out var next)
                        || next.Scheme != Uri.UriSchemeHttps)
                    {
                        return SignedUrlProbeResult.Failed("insecure redirect");
                    }

                    currentUri = next;
                    continue;
                }

                var statusCode = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    return new SignedUrlProbeResult(
                        statusCode,
                        statusCode.ToString(CultureInfo.InvariantCulture),
                        string.Empty);
                }

                var content = await ReadLimitedContentAsync(
                    response.Content,
                    maximumBytes,
                    timeout.Token);
                return new SignedUrlProbeResult(
                    statusCode,
                    statusCode.ToString(CultureInfo.InvariantCulture),
                    content);
            }

            return SignedUrlProbeResult.Failed("too many redirects");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SignedUrlProbeResult.Failed("timeout");
        }
        catch (HttpRequestException exception)
        {
            // The inner type separates a name resolution failure from a reset
            // connection, which a status code alone would not distinguish.
            var cause = exception.InnerException?.GetType().Name
                ?? exception.GetType().Name;
            return SignedUrlProbeResult.Failed(cause);
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or (HttpStatusCode)308;
    }

    /// <summary>
    /// Reads at most the requested number of bytes and abandons the rest. A live
    /// segment is several megabytes; the status and the first bytes are enough to
    /// prove the signed URL is still served.
    /// </summary>
    private static async Task<string> ReadLimitedContentAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        var total = 0;
        while (total < maximumBytes)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes - total)),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            total += read;
        }

        return builder.ToString();
    }
}

internal sealed class SignedUrlProbeResult
{
    public SignedUrlProbeResult(int? statusCode, string status, string content)
    {
        StatusCode = statusCode;
        Status = status;
        Content = content;
    }

    /// <summary>Gets the HTTP status code, or null when the request never completed.</summary>
    public int? StatusCode { get; }

    /// <summary>Gets the status code, or the reason the request could not complete.</summary>
    public string Status { get; }

    public string Content { get; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public static SignedUrlProbeResult Failed(string reason)
    {
        return new SignedUrlProbeResult(null, reason, string.Empty);
    }
}
