using System.Net;

namespace Emby.Zattoo.Exceptions
{
    public sealed class ZattooRateLimitException : ZattooApiException
    {
        public ZattooRateLimitException(string message, HttpStatusCode? statusCode = null)
            : base(message, statusCode)
        {
        }
    }
}
