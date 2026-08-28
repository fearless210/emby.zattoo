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

        Task<IReadOnlyList<ZattooProgram>> GetProgramsAsync(
            IReadOnlyCollection<string> channelIds,
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            CancellationToken cancellationToken = default);

        void SetImportedGuideChannels(IReadOnlyCollection<string> channelIds);

        Task<IReadOnlyList<ZattooProgramDetails>> GetProgramDetailsAsync(
            IReadOnlyCollection<string> programIds,
            CancellationToken cancellationToken = default);

        Task<ZattooFieldInventory> SurveyFieldsAsync(
            CancellationToken cancellationToken = default);

        Task<ZattooGuideEndpointComparison> CompareGuideEndpointsAsync(
            DateTimeOffset startTime,
            DateTimeOffset endTime,
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

        void StopGuideEnrichment();

        void PrioritizeGuideDetails(string channelId);

        void Invalidate();
    }
}
