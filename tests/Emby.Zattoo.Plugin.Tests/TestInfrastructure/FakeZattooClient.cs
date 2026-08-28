using Emby.Zattoo.Models;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Plugin.Tests.TestInfrastructure;

/// <summary>Records lifecycle calls; every protocol member stays unimplemented.</summary>
internal sealed class FakeZattooClient : IZattooClient
{
    public FakeZattooClient(string name = "client")
    {
        Name = name;
    }

    public string Name { get; }

    public int DisposeCount { get; private set; }

    public bool IsAuthenticated => false;

    public DateTimeOffset? SessionCreatedAt => null;

    public ZattooSessionInfo? SessionInfo => null;

    public Task LoginAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<ZattooChannel>> GetChannelsAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<ZattooProgram>> GetProgramsAsync(
        IReadOnlyCollection<string> channelIds,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public void SetImportedGuideChannels(IReadOnlyCollection<string> channelIds) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<ZattooProgramDetails>> GetProgramDetailsAsync(
        IReadOnlyCollection<string> programIds,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<ZattooFieldInventory> SurveyFieldsAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<ZattooGuideEndpointComparison> CompareGuideEndpointsAsync(
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<ZattooStream>> GetStreamOptionsAsync(
        string channelId,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<ZattooStream> GetStreamAsync(
        string channelId,
        ZattooPreferredQuality preferredQuality = ZattooPreferredQuality.Auto,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<ZattooStream> GetStreamAsync(
        string channelId,
        ZattooPreferredQuality preferredQuality,
        ZattooStreamFormat preferredFormat,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public void StopGuideEnrichment() =>
        throw new NotImplementedException();

    public void PrioritizeGuideDetails(string channelId) =>
        throw new NotImplementedException();

    public void Invalidate() =>
        throw new NotImplementedException();

    public void Dispose()
    {
        DisposeCount++;
    }
}
