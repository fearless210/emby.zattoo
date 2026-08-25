namespace Emby.Zattoo.Exceptions
{
    public sealed class ZattooTransportException : ZattooException
    {
        public ZattooTransportException(string message)
            : base(message)
        {
        }
    }
}
