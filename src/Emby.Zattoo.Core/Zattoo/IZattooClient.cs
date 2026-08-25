using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Zattoo
{
    public interface IZattooClient : IDisposable
    {
        bool IsAuthenticated { get; }

        DateTimeOffset? SessionCreatedAt { get; }

        ZattooSessionInfo? SessionInfo { get; }

        Task LoginAsync(CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ZattooChannel>> GetChannelsAsync(
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ZattooStream>> GetStreamOptionsAsync(
            string channelId,
            CancellationToken cancellationToken = default);

        Task<ZattooStream> GetStreamAsync(
            string channelId,
            ZattooPreferredQuality preferredQuality = ZattooPreferredQuality.Auto,
            CancellationToken cancellationToken = default);

        Task<ZattooStream> GetStreamAsync(
            string channelId,
            ZattooPreferredQuality preferredQuality,
            ZattooStreamFormat preferredFormat,
            CancellationToken cancellationToken = default);

        void Invalidate();
    }
}
