using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Infrastructure
{
    /// <summary>Server-side HTTP transport with a private cookie jar.</summary>
    public sealed class ZattooHttpTransport : IZattooTransport
    {
        private readonly Uri baseUri;
        private readonly CookieContainer cookies;
        private readonly HttpClient client;
        private bool disposed;

        public ZattooHttpTransport(ZattooClientOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            baseUri = options.ProviderBaseUri;
            cookies = new CookieContainer();

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = cookies,
                UseCookies = true,
                AllowAutoRedirect = true,
            };

            client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = baseUri,
                Timeout = options.RequestTimeout,
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            client.DefaultRequestHeaders.Referrer = baseUri;
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Requested-With",
                "XMLHttpRequest");
            ResetSession(options.DeviceId);
        }

        public Task<ZattooTransportResponse> GetAsync(
            string relativePath,
            CancellationToken cancellationToken)
        {
            return SendAsync(HttpMethod.Get, relativePath, fields: null, cancellationToken);
        }

        public Task<ZattooTransportResponse> PostFormAsync(
            string relativePath,
            IReadOnlyDictionary<string, string> fields,
            CancellationToken cancellationToken)
        {
            if (fields == null)
            {
                throw new ArgumentNullException(nameof(fields));
            }

            return SendAsync(HttpMethod.Post, relativePath, fields, cancellationToken);
        }

        public void ResetSession(string deviceId)
        {
            ThrowIfDisposed();

            foreach (Cookie cookie in cookies.GetCookies(baseUri))
            {
                cookie.Expired = true;
            }

            cookies.Add(baseUri, new Cookie("uuid", deviceId, "/"));
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            client.Dispose();
        }

        private async Task<ZattooTransportResponse> SendAsync(
            HttpMethod method,
            string relativePath,
            IReadOnlyDictionary<string, string>? fields,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var requestUri = CreateRequestUri(relativePath);

            using (var request = new HttpRequestMessage(method, requestUri))
            {
                if (fields != null)
                {
                    var content = new FormUrlEncodedContent(fields);
                    if (content.Headers.ContentType != null)
                    {
                        content.Headers.ContentType.CharSet = "UTF-8";
                    }

                    request.Content = content;
                }

                try
                {
                    using (var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return new ZattooTransportResponse(response.StatusCode, content);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ZattooTransportException("The Zattoo HTTP request timed out.");
                }
                catch (HttpRequestException)
                {
                    // HttpRequestException can contain the full request URI. Do not retain it.
                    throw new ZattooTransportException("The Zattoo HTTP request failed.");
                }
            }
        }

        internal Uri CreateRequestUri(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A relative path is required.", nameof(relativePath));
            }

            var normalized = relativePath.Trim();
            if (normalized.StartsWith("//", StringComparison.Ordinal)
                || normalized.IndexOf('\\') >= 0
                || normalized.IndexOfAny(new[] { '\r', '\n' }) >= 0
                || (!normalized.StartsWith("/", StringComparison.Ordinal)
                    && Uri.TryCreate(normalized, UriKind.Absolute, out _)))
            {
                throw new ArgumentException("Only provider-relative paths are allowed.", nameof(relativePath));
            }

            var requestUri = new Uri(baseUri, normalized);
            if (!string.Equals(requestUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(requestUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
                || requestUri.Port != baseUri.Port)
            {
                throw new ArgumentException("Only provider-relative paths are allowed.", nameof(relativePath));
            }

            return requestUri;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ZattooHttpTransport));
            }
        }
    }
}
