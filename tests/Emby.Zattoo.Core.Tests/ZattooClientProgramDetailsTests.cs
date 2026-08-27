using System.Net;
using System.Net.Http;
using Emby.Zattoo.Core.Tests.TestInfrastructure;
using Emby.Zattoo.Exceptions;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooClientProgramDetailsTests
{
    private const string DetailsPath =
        "/zapi/v2/cached/program/power_details/fixture-guide-hash?complete=True&program_ids=1002,2001";

    [Fact]
    public async Task GetProgramDetailsAsync_MapsDeduplicatesAndBoundsRequest()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        QueueDetails(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var details = await client.GetProgramDetailsAsync(
            new[] { "1002", "1002", "2001" });

        Assert.Collection(
            details,
            first =>
            {
                Assert.Equal("1002", first.Id);
                Assert.Equal("Épisode détaillé fictif", first.EpisodeTitle);
                Assert.Equal(
                    "Description détaillée fictive utilisée uniquement par les tests.",
                    first.Overview);
                Assert.Equal(new[] { "Magazine", "Culture" }, first.Genres);
                Assert.Equal(2, first.SeasonNumber);
                Assert.Equal(3, first.EpisodeNumber);
            },
            second =>
            {
                Assert.Equal("2001", second.Id);
                Assert.Null(second.Overview);
                Assert.Empty(second.Genres);
                Assert.Null(second.SeasonNumber);
                Assert.Null(second.EpisodeNumber);
            });
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramDetailsAsync_RejectsInvalidBatchBeforeAuthentication()
    {
        var transport = new FakeZattooTransport();
        using var client = ZattooClientAuthenticationTests.CreateClient(transport);

        Assert.Empty(await client.GetProgramDetailsAsync(Array.Empty<string>()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetProgramDetailsAsync(new[] { " " }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetProgramDetailsAsync(
                Enumerable.Range(1, 21).Select(value => value.ToString()).ToArray()));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramDetailsAsync_RenewsSessionOnceAfterForbiddenResponse()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(HttpMethod.Get, DetailsPath, HttpStatusCode.Forbidden, string.Empty);
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        QueueDetails(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var details = await client.GetProgramDetailsAsync(new[] { "1002", "2001" });

        Assert.Equal(2, details.Count);
        Assert.Equal(2, transport.ResetCount);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramDetailsAsync_RejectsInvalidDocument()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            DetailsPath,
            HttpStatusCode.OK,
            "{\"success\":true}");

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);

        await Assert.ThrowsAsync<ZattooProtocolException>(() =>
            client.GetProgramDetailsAsync(new[] { "1002", "2001" }));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    private static void QueueDetails(FakeZattooTransport transport)
    {
        transport.Enqueue(
            HttpMethod.Get,
            DetailsPath,
            HttpStatusCode.OK,
            Fixture.Read("program-details.json"));
    }
}
