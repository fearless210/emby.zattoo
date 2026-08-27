using System.Net;
using System.Net.Http;
using Emby.Zattoo.Core.Tests.TestInfrastructure;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Models;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooClientGuideTests
{
    private const string GuidePath =
        "/zapi/v3/cached/fixture-guide-hash/guide?end=1800018000&start=1800000000&format=json";

    [Fact]
    public async Task CompareGuideEndpointsAsync_ReportsMetadataWithoutReturningContent()
    {
        const string legacyPath =
            "/zapi/v2/cached/program/power_guide/fixture-guide-hash?end=1800003600&start=1800000000";
        const string currentPath =
            "/zapi/v3/cached/fixture-guide-hash/guide?end=1800003600&start=1800000000&format=json";
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            legacyPath,
            HttpStatusCode.OK,
            "{\"success\":true,\"channels\":[{\"cid\":\"one\",\"programs\":["
                + "{\"id\":1,\"s\":1800000000,\"e\":1800001200,\"t\":\"Shared\",\"d\":\"Legacy detail\",\"g\":[\"News\"]},"
                + "{\"id\":2,\"s\":1800001200,\"e\":1800002400,\"t\":\"Legacy only\"}]}]}");
        transport.Enqueue(
            HttpMethod.Get,
            currentPath,
            HttpStatusCode.OK,
            "{\"success\":true,\"channels\":{\"one\":["
                + "{\"id\":1,\"s\":1800000000,\"e\":1800001200,\"t\":\"Shared\"},"
                + "{\"id\":3,\"s\":1800002400,\"e\":1800003600,\"t\":\"Current only\",\"d\":\"Current detail\"}]}}");

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var comparison = await client.CompareGuideEndpointsAsync(
            DateTimeOffset.FromUnixTimeSeconds(1800000000),
            DateTimeOffset.FromUnixTimeSeconds(1800003600));

        Assert.Equal(2, comparison.Version2.Programs);
        Assert.Equal(1, comparison.Version2.ProgramsWithDescription);
        Assert.Equal(1, comparison.Version2.ProgramsWithGenres);
        Assert.Equal(2, comparison.Version3.Programs);
        Assert.Equal(1, comparison.Version3.ProgramsWithDescription);
        Assert.Equal(1, comparison.SharedPrograms);
        Assert.Equal(1, comparison.Version2OnlyPrograms);
        Assert.Equal(1, comparison.Version3OnlyPrograms);
        Assert.Equal(1, comparison.SharedDescriptionsOnlyInVersion2);
        Assert.Equal(0, comparison.SharedDescriptionsOnlyInVersion3);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramsAsync_EnrichesGuideInBackgroundWithoutDuplicateRequests()
    {
        const long windowSeconds = 5 * 60 * 60;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var windowStart = ((nowUnix / windowSeconds) + 1) * windowSeconds;
        var windowEnd = windowStart + windowSeconds;
        var guidePath =
            $"/zapi/v3/cached/fixture-guide-hash/guide?end={windowEnd}&start={windowStart}&format=json";
        const string detailsPath =
            "/zapi/v2/cached/program/power_details/fixture-guide-hash?complete=True&program_ids=4001,4002";
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            guidePath,
            HttpStatusCode.OK,
            $"{{\"success\":true,\"channels\":{{"
                + $"\"tsr1\":[{{\"id\":4001,\"s\":{windowStart + 60},\"e\":{windowStart + 1800},\"t\":\"First fixture\"}}],"
                + $"\"tsr2\":[{{\"id\":4002,\"s\":{windowStart + 120},\"e\":{windowStart + 1900},\"t\":\"Second fixture\"}}]}}}}");
        transport.Enqueue(
            HttpMethod.Get,
            detailsPath,
            HttpStatusCode.OK,
            "{\"success\":true,\"programs\":[{\"id\":4002,\"d\":\"Background detail fixture.\",\"g\":[\"Documentary\"]}]}");
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new ZattooClient(
            new ZattooClientOptions
            {
                Username = "fixture-user",
                Password = "fixture-password",
                DeviceId = "fixture-device-id",
                EnableBackgroundGuideDetails = true,
                GuideDetailsRequestInterval = TimeSpan.Zero,
                GuideDetailsRetryDelay = TimeSpan.Zero,
                GuideDetailsProgress = progress =>
                {
                    if (progress.Kind == ZattooGuideDetailsProgressKind.Completed)
                    {
                        completed.TrySetResult(true);
                    }
                },
            },
            transport);
        var start = DateTimeOffset.FromUnixTimeSeconds(windowStart);
        var end = DateTimeOffset.FromUnixTimeSeconds(windowStart + 3600);

        var initial = await client.GetProgramsAsync(new[] { "tsr1" }, start, end);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var enriched = await client.GetProgramsAsync(new[] { "tsr2" }, start, end);
        var repeated = await client.GetProgramsAsync(new[] { "tsr2" }, start, end);

        Assert.Single(initial);
        Assert.Equal("Background detail fixture.", Assert.Single(enriched).Overview);
        Assert.Equal(new[] { "Documentary" }, Assert.Single(repeated).Genres);
        Assert.Equal(
            1,
            transport.RecordedRequests.Count(
                request => request.RelativePath == detailsPath));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramsAsync_MapsFiltersAndCachesGuidePrograms()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        QueueGuide(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var start = DateTimeOffset.FromUnixTimeSeconds(1800000300);
        var end = DateTimeOffset.FromUnixTimeSeconds(1800011100);

        var firstChannel = await client.GetProgramsAsync(new[] { "tsr1" }, start, end);
        var secondChannel = await client.GetProgramsAsync(new[] { "tsr2" }, start, end);

        Assert.Collection(
            firstChannel,
            first =>
            {
                Assert.Equal("1001", first.Id);
                Assert.Equal("tsr1", first.ChannelId);
                Assert.Equal("Programme commencé", first.Name);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), first.StartDate);
            },
            second =>
            {
                Assert.Equal("1002", second.Id);
                Assert.Equal("Magazine fictif", second.Name);
                Assert.Equal("Épisode pilote", second.EpisodeTitle);
                Assert.Equal(
                    "Description fictive utilisée uniquement par les tests.",
                    second.Overview);
                Assert.Equal(new[] { "Magazine", "Culture" }, second.Genres);
                Assert.Equal(2, second.SeasonNumber);
                Assert.Equal(3, second.EpisodeNumber);
                Assert.Equal(
                    "https://images.zattic.com/cms/fixture-image-token/format_480x360.jpg",
                    second.ImageUrl);
            });

        var secondProgram = Assert.Single(secondChannel);
        Assert.Equal("2001", secondProgram.Id);
        Assert.Equal("tsr2", secondProgram.ChannelId);
        Assert.Equal(
            "https://images.zattic.com/cms/fixture/format_480x360.jpg",
            secondProgram.ImageUrl);
        Assert.Equal(
            1,
            transport.RecordedRequests.Count(request => request.RelativePath == GuidePath));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramsAsync_ParsesOnlyImportedGuideChannels()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        QueueGuide(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        client.SetImportedGuideChannels(new[] { "tsr1" });
        var start = DateTimeOffset.FromUnixTimeSeconds(1800000300);
        var end = DateTimeOffset.FromUnixTimeSeconds(1800011100);

        var imported = await client.GetProgramsAsync(new[] { "tsr1" }, start, end);
        var excluded = await client.GetProgramsAsync(new[] { "tsr2" }, start, end);

        Assert.Equal(2, imported.Count);
        Assert.Empty(excluded);
        Assert.Equal(
            1,
            transport.RecordedRequests.Count(request => request.RelativePath == GuidePath));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramsAsync_CoalescesConcurrentWindowLoads()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        var release = transport.EnqueueDeferred(
            HttpMethod.Get,
            GuidePath,
            HttpStatusCode.OK,
            Fixture.Read("guide.json"));

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var start = DateTimeOffset.FromUnixTimeSeconds(1800000300);
        var end = DateTimeOffset.FromUnixTimeSeconds(1800003600);

        var first = client.GetProgramsAsync(new[] { "tsr1" }, start, end);
        var second = client.GetProgramsAsync(new[] { "tsr2" }, start, end);

        Assert.Equal(
            1,
            transport.RecordedRequests.Count(request => request.RelativePath == GuidePath));
        release.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramsAsync_SupportsFourteenDayRangeAndReusesEveryWindow()
    {
        const long startUnix = 1800000000;
        const int windowCount = 68;
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        for (var index = 0; index < windowCount; index++)
        {
            var windowStart = startUnix + (index * 18000L);
            var windowEnd = windowStart + 18000L;
            transport.Enqueue(
                HttpMethod.Get,
                $"/zapi/v3/cached/fixture-guide-hash/guide?end={windowEnd}&start={windowStart}&format=json",
                HttpStatusCode.OK,
                "{\"success\":true,\"channels\":{}}");
        }

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var start = DateTimeOffset.FromUnixTimeSeconds(startUnix);
        var end = start.AddDays(14);

        var first = await client.GetProgramsAsync(new[] { "tsr1" }, start, end);
        var second = await client.GetProgramsAsync(new[] { "tsr2" }, start, end);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal(
            windowCount,
            transport.RecordedRequests.Count(
                request => request.RelativePath.Contains("/guide?", StringComparison.Ordinal)));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramsAsync_RenewsSessionOnceAfterForbiddenResponse()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(HttpMethod.Get, GuidePath, HttpStatusCode.Forbidden, string.Empty);
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        QueueGuide(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var programs = await client.GetProgramsAsync(
            new[] { "tsr1" },
            DateTimeOffset.FromUnixTimeSeconds(1800000300),
            DateTimeOffset.FromUnixTimeSeconds(1800003600));

        Assert.NotEmpty(programs);
        Assert.Equal(2, transport.ResetCount);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetProgramsAsync_AcceptsLegacyChannelArrayShape()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            GuidePath,
            HttpStatusCode.OK,
            Fixture.Read("guide-array.json"));

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var programs = await client.GetProgramsAsync(
            new[] { "tsr1" },
            DateTimeOffset.FromUnixTimeSeconds(1800000300),
            DateTimeOffset.FromUnixTimeSeconds(1800003600));

        var program = Assert.Single(programs);
        Assert.Equal("3001", program.Id);
        Assert.Equal("Ancien format de guide", program.Name);
    }

    [Fact]
    public async Task GetProgramsAsync_RejectsInvalidRangeAndDocument()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            GuidePath,
            HttpStatusCode.OK,
            "{\"success\":true}");

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var start = DateTimeOffset.FromUnixTimeSeconds(1800000300);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetProgramsAsync(new[] { "tsr1" }, start, start));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetProgramsAsync(
                new[] { "tsr1" },
                start,
                start.AddDays(14).AddTicks(1)));
        await Assert.ThrowsAsync<ZattooProtocolException>(() =>
            client.GetProgramsAsync(
                new[] { "tsr1" },
                start,
                DateTimeOffset.FromUnixTimeSeconds(1800003600)));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    private static void QueueGuide(FakeZattooTransport transport)
    {
        transport.Enqueue(
            HttpMethod.Get,
            GuidePath,
            HttpStatusCode.OK,
            Fixture.Read("guide.json"));
    }
}
