namespace Emby.Zattoo.Exceptions
{
    public sealed class ZattooStreamUnavailableException : ZattooException
    {
        public ZattooStreamUnavailableException(string message)
            : base(message)
        {
        }

        public ZattooStreamUnavailableException(string message, System.Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
