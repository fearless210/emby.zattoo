using System.Net;

namespace Emby.Zattoo.Infrastructure
{
    public sealed class ZattooTransportResponse
    {
        public ZattooTransportResponse(HttpStatusCode statusCode, string content)
        {
            StatusCode = statusCode;
            Content = content;
        }

        public HttpStatusCode StatusCode { get; }

        public string Content { get; }

        public bool IsSuccessStatusCode => (int)StatusCode >= 200 && (int)StatusCode <= 299;
    }
}
