using System;

namespace Emby.Zattoo.Exceptions
{
    public class ZattooException : Exception
    {
        public ZattooException(string message)
            : base(message)
        {
        }

        public ZattooException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
