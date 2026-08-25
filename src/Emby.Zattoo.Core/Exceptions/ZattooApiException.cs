using System.Net;

namespace Emby.Zattoo.Exceptions
{
    public class ZattooApiException : ZattooException
    {
        public ZattooApiException(string message, HttpStatusCode? statusCode = null)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode? StatusCode { get; }
    }
}
