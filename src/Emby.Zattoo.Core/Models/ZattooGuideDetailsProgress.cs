namespace Emby.Zattoo.Models
{
    public enum ZattooGuideDetailsProgressKind
    {
        Started,
        Progress,
        Retrying,
        Completed,
        Stopped,
    }

    public sealed class ZattooGuideDetailsProgress
    {
        public ZattooGuideDetailsProgressKind Kind { get; set; }

        public int PendingPrograms { get; set; }

        public int CachedPrograms { get; set; }

        public long ProcessedPrograms { get; set; }

        public long FailedBatches { get; set; }

        public long RemovedPrograms { get; set; }
    }
}
