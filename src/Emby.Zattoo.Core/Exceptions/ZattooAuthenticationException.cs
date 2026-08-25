using System.Net;

namespace Emby.Zattoo.Exceptions
{
    public sealed class ZattooAuthenticationException : ZattooApiException
    {
        public ZattooAuthenticationException(string message, HttpStatusCode? statusCode = null)
            : base(message, statusCode)
        {
        }
    }
}
