using System;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Zattoo.Plugin.LiveTv
{
    /// <summary>
    /// Serializes the consumers of one FFmpeg standard output. A pipe cannot be
    /// read twice at once, but Emby does attach again after a first consumer is
    /// done, for instance when media detection precedes a transcode. A short wait
    /// also absorbs the overlap between a consumer leaving and the next arriving.
    /// </summary>
    internal sealed class ZattooStreamConsumerGate
    {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        public async Task<bool> TryEnterAsync(
            TimeSpan handoverTimeout,
            CancellationToken cancellationToken)
        {
            if (handoverTimeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(handoverTimeout));
            }

            return await gate.WaitAsync(handoverTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        public void Exit()
        {
            gate.Release();
        }
    }
}
