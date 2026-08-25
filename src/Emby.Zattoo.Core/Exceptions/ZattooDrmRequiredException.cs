namespace Emby.Zattoo.Exceptions
{
    public sealed class ZattooDrmRequiredException : ZattooException
    {
        public ZattooDrmRequiredException(string message)
            : base(message)
        {
        }
    }
}
