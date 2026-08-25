using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Zattoo.Exceptions;

namespace Emby.Zattoo.Zattoo
{
    /// <summary>Loads a signed HLS master in memory and selects secure media playlists.</summary>
    public static class HlsManifestResolver
    {
        private const int MaximumRedirects = 3;
        private const int MaximumPlaylistBytes = 1024 * 1024;

        public static async Task<HlsPlaylistSelection> ResolveAsync(
            string playlistUrl,
            int? maximumHeight,
            CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(playlistUrl, UriKind.Absolute, out var playlistUri)
                || playlistUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ZattooProtocolException(
                    "The HLS master URL is invalid or insecure.");
            }

            using (var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
            })
            using (var client = new HttpClient(handler, disposeHandler: true))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var currentUri = playlistUri;
                for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, currentUri))
                    {
                        request.Headers.Accept.Add(
                            new MediaTypeWithQualityHeaderValue("application/vnd.apple.mpegurl"));
                        request.Headers.Accept.Add(
                            new MediaTypeWithQualityHeaderValue("*/*", 0.5));

                        using (var response = await client.SendAsync(
                                request,
                                HttpCompletionOption.ResponseHeadersRead,
                                timeout.Token)
                            .ConfigureAwait(false))
                        {
                            if (IsRedirect(response.StatusCode))
                            {
                                if (redirect == MaximumRedirects
                                    || response.Headers.Location == null)
                                {
                                    throw new ZattooProtocolException(
                                        "The signed HLS manifest redirected too many times.");
                                }

                                currentUri = ResolveSecureRedirect(
                                    currentUri,
                                    response.Headers.Location);
                                continue;
                            }

                            if (!response.IsSuccessStatusCode)
                            {
                                throw new ZattooProtocolException(
                                    string.Format(
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        "The signed HLS manifest could not be loaded (HTTP {0}).",
                                        (int)response.StatusCode));
                            }

                            var content = await ReadLimitedContentAsync(
                                    response.Content,
                                    timeout.Token)
                                .ConfigureAwait(false);
                            return HlsPlaylistSelector.Select(
                                content,
                                currentUri,
                                maximumHeight);
                        }
                    }
                }
            }

            throw new ZattooProtocolException(
                "The signed HLS manifest could not be resolved.");
        }

        private static async Task<string> ReadLimitedContentAsync(
            HttpContent content,
            CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength > MaximumPlaylistBytes)
            {
                throw new ZattooProtocolException("The HLS playlist is unexpectedly large.");
            }

            using (var source = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var destination = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await source.ReadAsync(
                            buffer,
                            0,
                            buffer.Length,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (destination.Length + read > MaximumPlaylistBytes)
                    {
                        throw new ZattooProtocolException(
                            "The HLS playlist is unexpectedly large.");
                    }

                    await destination.WriteAsync(buffer, 0, read, cancellationToken)
                        .ConfigureAwait(false);
                }

                return Encoding.UTF8.GetString(destination.ToArray());
            }
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.MovedPermanently
                || statusCode == HttpStatusCode.Redirect
                || statusCode == HttpStatusCode.RedirectMethod
                || statusCode == HttpStatusCode.TemporaryRedirect
                || (int)statusCode == 308;
        }

        private static Uri ResolveSecureRedirect(Uri currentUri, Uri location)
        {
            if (!Uri.TryCreate(currentUri, location, out var result)
                || result.Scheme != Uri.UriSchemeHttps)
            {
                throw new ZattooProtocolException(
                    "The signed HLS manifest redirected to an invalid or insecure URI.");
            }

            return result;
        }
    }
}
