using System.Net;

namespace Emby.Zattoo.Exceptions
{
    public sealed class ZattooSessionExpiredException : ZattooApiException
    {
        public ZattooSessionExpiredException(string message, HttpStatusCode? statusCode = null)
            : base(message, statusCode)
        {
        }
    }
}
