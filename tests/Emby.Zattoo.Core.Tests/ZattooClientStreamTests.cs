using System.Net;
using System.Net.Http;
using Emby.Zattoo.Core.Tests.TestInfrastructure;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooClientStreamTests
{
    [Fact]
    public async Task GetStreamOptionsAsync_ReportsDrmWithoutRequestingWidevine()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);
        QueueSuccessfulStream(transport);
        QueueSuccessfulStream(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var streams = await client.GetStreamOptionsAsync("ch-open");

        Assert.Collection(
            streams,
            drm =>
            {
                Assert.Equal("1080p", drm.Quality);
                Assert.True(drm.DrmRequired);
                Assert.False(drm.IsSupported);
                Assert.Null(drm.Url);
            },
            hd =>
            {
                Assert.Equal("720p", hd.Quality);
                Assert.False(hd.DrmRequired);
                Assert.True(hd.IsSupported);
                Assert.Equal(6200, hd.BitrateKbps);
            },
            sd =>
            {
                Assert.Equal("540p", sd.Quality);
                Assert.False(sd.DrmRequired);
                Assert.True(sd.IsSupported);
            });

        var requests = transport.RecordedRequests
            .Where(request => request.RelativePath == "/zapi/watch")
            .ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("ch-open", request.Fields?["cid"]);
            Assert.Equal("dash", request.Fields?["stream_type"]);
            Assert.Equal("true", request.Fields?["https_watch_urls"]);
            Assert.Equal("json", request.Fields?["format"]);
            Assert.False(request.Fields?.ContainsKey("timeshift"));
            Assert.DoesNotContain(
                request.Fields!,
                field => field.Value.Contains("widevine", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetStreamAsync_SelectsOneBestNonDrmQuality()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);
        QueueSuccessfulStream(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var stream = await client.GetStreamAsync("ch-open");

        Assert.Equal("720p", stream.Quality);
        Assert.Equal(720, stream.Height);
        Assert.Equal(6200, stream.BitrateKbps);
        Assert.Equal(ZattooStreamFormat.Dash, stream.Format);
        Assert.True(stream.IsSupported);
        Assert.StartsWith("https://stream.invalid/", stream.Url, StringComparison.Ordinal);

        var request = Assert.Single(
            transport.RecordedRequests,
            item => item.RelativePath == "/zapi/watch");
        Assert.Equal("ch-open", request.Fields?["cid"]);
        Assert.Equal("hd", request.Fields?["quality"]);
        Assert.Equal("dash", request.Fields?["stream_type"]);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetStreamAsync_RespectsPreferredQuality()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);
        QueueSuccessfulStream(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var stream = await client.GetStreamAsync(
            "ch-open",
            ZattooPreferredQuality.P540);

        Assert.Equal("540p", stream.Quality);
        var request = Assert.Single(
            transport.RecordedRequests,
            item => item.RelativePath == "/zapi/watch");
        Assert.Equal("sd", request.Fields?["quality"]);
    }

    [Fact]
    public async Task GetStreamAsync_CanRequestNonDrmHls()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);
        QueueSuccessfulStream(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var stream = await client.GetStreamAsync(
            "ch-open",
            ZattooPreferredQuality.Auto,
            ZattooStreamFormat.Hls);

        Assert.Equal(ZattooStreamFormat.Hls, stream.Format);
        Assert.True(stream.IsSupported);
        var request = Assert.Single(
            transport.RecordedRequests,
            item => item.RelativePath == "/zapi/watch");
        Assert.Equal("hls7", request.Fields?["stream_type"]);
        Assert.Equal("true", request.Fields?["https_watch_urls"]);
        Assert.DoesNotContain(
            request.Fields!,
            field => field.Value.Contains("widevine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetStreamAsync_CanRequestNonDrmHlsMpegTs()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);
        QueueSuccessfulStream(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var stream = await client.GetStreamAsync(
            "ch-open",
            ZattooPreferredQuality.Auto,
            ZattooStreamFormat.MpegTs);

        Assert.Equal(ZattooStreamFormat.MpegTs, stream.Format);
        Assert.True(stream.IsSupported);
        var request = Assert.Single(
            transport.RecordedRequests,
            item => item.RelativePath == "/zapi/watch");
        Assert.Equal("hls", request.Fields?["stream_type"]);
        Assert.Equal("true", request.Fields?["https_watch_urls"]);
        Assert.DoesNotContain(
            request.Fields!,
            field => field.Value.Contains("widevine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetStreamAsync_DrmOnlyChannelNeverRequestsPlaybackUrl()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);

        await Assert.ThrowsAsync<ZattooDrmRequiredException>(
            () => client.GetStreamAsync("ch-drm"));
        Assert.DoesNotContain(
            transport.RecordedRequests,
            request => request.RelativePath.StartsWith("/zapi/watch", StringComparison.Ordinal));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetStreamAsync_TreatsLicenseResponseAsUnsupportedDrm()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);
        transport.Enqueue(
            HttpMethod.Post,
            "/zapi/watch",
            HttpStatusCode.OK,
            Fixture.Read("stream-drm.json"));

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);

        await Assert.ThrowsAsync<ZattooDrmRequiredException>(
            () => client.GetStreamAsync("ch-open"));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetStreamAsync_RenewsOnceAfterForbiddenResponse()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);
        transport.Enqueue(
            HttpMethod.Post,
            "/zapi/watch",
            HttpStatusCode.Forbidden,
            string.Empty);
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        QueueSuccessfulStream(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var stream = await client.GetStreamAsync("ch-open");

        Assert.True(stream.IsSupported);
        Assert.Equal(2, transport.ResetCount);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetStreamAsync_DoesNotLoopAfterSecondForbiddenResponse()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);
        transport.Enqueue(
            HttpMethod.Post,
            "/zapi/watch",
            HttpStatusCode.Forbidden,
            string.Empty);
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Post,
            "/zapi/watch",
            HttpStatusCode.Forbidden,
            string.Empty);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);

        await Assert.ThrowsAsync<ZattooSessionExpiredException>(
            () => client.GetStreamAsync("ch-open"));
        Assert.False(client.IsAuthenticated);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetChannelsAsync_ProvidesCatalogueStatisticsWithoutOpeningStreams()
    {
        var transport = new FakeZattooTransport();
        QueueStreamCatalogue(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();
        var statistics = ZattooStreamStatistics.Calculate(channels);

        Assert.Equal(3, statistics.TotalChannels);
        Assert.Equal(2, statistics.ChannelsWithAvailableStreams);
        Assert.Equal(1, statistics.ChannelsWithNonDrmStreams);
        Assert.Equal(1, statistics.DrmOnlyChannels);
        Assert.Equal(1, statistics.ChannelsWithoutAvailableStreams);
        Assert.DoesNotContain(
            transport.RecordedRequests,
            request => request.RelativePath.StartsWith("/zapi/watch", StringComparison.Ordinal));
    }

    private static void QueueStreamCatalogue(FakeZattooTransport transport)
    {
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/channels/favorites",
            HttpStatusCode.OK,
            Fixture.Read("favorites.json"));
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/v3/cached/fixture-guide-hash/channels",
            HttpStatusCode.OK,
            Fixture.Read("channels-streams.json"));
    }

    private static void QueueSuccessfulStream(FakeZattooTransport transport)
    {
        transport.Enqueue(
            HttpMethod.Post,
            "/zapi/watch",
            HttpStatusCode.OK,
            Fixture.Read("stream-success.json"));
    }
}
