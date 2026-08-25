namespace Emby.Zattoo.Exceptions
{
    public sealed class ZattooProtocolException : ZattooException
    {
        public ZattooProtocolException(string message)
            : base(message)
        {
        }
    }
}
