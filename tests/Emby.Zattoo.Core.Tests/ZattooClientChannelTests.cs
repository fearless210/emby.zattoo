using System.Net;
using System.Net.Http;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Core.Tests.TestInfrastructure;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooClientChannelTests
{
    [Fact]
    public async Task GetChannelsAsync_MapsStableIdsNamesFavoritesAndLogos()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        QueueChannels(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();

        Assert.Collection(
            channels,
            first =>
            {
                Assert.Equal("ch-rts1", first.Id);
                Assert.Equal("RTS 1", first.Name);
                Assert.Equal(1, first.Number);
                Assert.True(first.IsFavorite);
                Assert.Equal("https://logos.zattic.com/logos/rts1.png", first.LogoUrl);
                var quality = Assert.Single(first.Qualities);
                Assert.Equal("hd", quality.Level);
                Assert.Equal(720, quality.Height);
                Assert.True(quality.IsAvailable);
                Assert.False(quality.DrmRequired);
            },
            second =>
            {
                Assert.Equal("ch-rts2", second.Id);
                Assert.Equal("RTS 2", second.Name);
                Assert.Equal(2, second.Number);
                Assert.False(second.IsFavorite);
                Assert.Equal("https://logos.zattic.com/logos/rts2.png", second.LogoUrl);
                Assert.Equal(2, second.Qualities.Count);
            });
        Assert.Equal(2, client.SessionInfo?.PlayableChannelCount);
        Assert.Equal(0, client.SessionInfo?.DrmOnlyChannelCount);
        Assert.Equal(0, client.SessionInfo?.UnavailableChannelCount);
        Assert.Equal(720, client.SessionInfo?.MaximumPlayableHeight);
        Assert.True(client.SessionInfo?.FavoritesAvailable);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetChannelsAsync_ResolvesTheGroupNameAndRadioFlag()
    {
        var transport = new FakeZattooTransport();
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
            "{\"groups\":[{\"name\":\"Suisse\"},{\"name\":\"Radio\"}],\"channels\":["
                + "{\"cid\":\"ch-a\",\"group_index\":0,\"is_radio\":false,"
                + "\"qualities\":[{\"availability\":\"available\",\"level\":\"hd\",\"title\":\"A\"}]},"
                + "{\"cid\":\"ch-b\",\"group_index\":1,\"is_radio\":true,"
                + "\"qualities\":[{\"availability\":\"available\",\"level\":\"sd\",\"title\":\"B\"}]},"
                + "{\"cid\":\"ch-c\",\"group_index\":9,"
                + "\"qualities\":[{\"availability\":\"available\",\"level\":\"sd\",\"title\":\"C\"}]}"
                + "]}");

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();

        Assert.Equal("Suisse", channels[0].GroupName);
        Assert.False(channels[0].IsRadio);
        Assert.Equal("Radio", channels[1].GroupName);
        Assert.True(channels[1].IsRadio);

        // An index outside the published groups leaves the channel ungrouped
        // rather than borrowing someone else's name.
        Assert.Equal(string.Empty, channels[2].GroupName);
    }

    [Fact]
    public async Task GetChannelsAsync_UsesTheChannelNumberPublishedByTheProvider()
    {
        var transport = new FakeZattooTransport();
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
            "{\"channels\":["
                + "{\"cid\":\"ch-a\",\"number\":41,\"qualities\":[{\"availability\":\"available\",\"level\":\"hd\",\"title\":\"A\"}]},"
                + "{\"cid\":\"ch-b\",\"number\":7,\"qualities\":[{\"availability\":\"available\",\"level\":\"hd\",\"title\":\"B\"}]}"
                + "]}");

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();

        // The provider number wins over the catalogue position, so adding or
        // removing a channel cannot renumber the ones that follow it.
        Assert.Equal(41, channels[0].Number);
        Assert.Equal(7, channels[1].Number);
    }

    [Fact]
    public async Task GetChannelsAsync_NumbersByPositionWhenTheProviderPublishesNone()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        QueueChannels(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();

        Assert.Equal(1, channels[0].Number);
        Assert.Equal(2, channels[1].Number);
    }

    [Fact]
    public async Task GetChannelsAsync_LoadsCatalogueWhenFavoritesRequestFails()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/channels/favorites",
            HttpStatusCode.InternalServerError,
            string.Empty);
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/v3/cached/fixture-guide-hash/channels",
            HttpStatusCode.OK,
            Fixture.Read("channels.json"));

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();

        Assert.Equal(2, channels.Count);
        Assert.All(channels, channel => Assert.False(channel.IsFavorite));
        Assert.False(client.SessionInfo?.FavoritesAvailable);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetChannelsAsync_LoadsCatalogueWhenFavoritesAreMalformed()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/channels/favorites",
            HttpStatusCode.OK,
            "{\"success\":false}");
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/v3/cached/fixture-guide-hash/channels",
            HttpStatusCode.OK,
            Fixture.Read("channels.json"));

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();

        Assert.Equal(2, channels.Count);
        Assert.False(client.SessionInfo?.FavoritesAvailable);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetChannelsAsync_RenewsSessionWhenFavoritesStayUnauthorized()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/channels/favorites",
            HttpStatusCode.Unauthorized,
            string.Empty);
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/channels/favorites",
            HttpStatusCode.Unauthorized,
            string.Empty);
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/v3/cached/fixture-guide-hash/channels",
            HttpStatusCode.OK,
            Fixture.Read("channels.json"));

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();

        Assert.Equal(2, channels.Count);
        Assert.False(client.SessionInfo?.FavoritesAvailable);
        Assert.True(client.IsAuthenticated);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetChannelsAsync_RenewsOnceAfterForbiddenResponse()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(HttpMethod.Get, "/zapi/channels/favorites", HttpStatusCode.Forbidden, string.Empty);
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        QueueChannels(transport);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();

        Assert.Equal(2, channels.Count);
        Assert.Equal(2, transport.ResetCount);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetChannelsAsync_DropsLogoWithUnsupportedScheme()
    {
        var transport = new FakeZattooTransport();
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
            Fixture.Read("channels.json").Replace(
                "\"/logos/rts1.png\"",
                "\"file:///tmp/rts1.png\""));

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);
        var channels = await client.GetChannelsAsync();

        Assert.Null(channels[0].LogoUrl);
        Assert.Equal("https://logos.zattic.com/logos/rts2.png", channels[1].LogoUrl);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetChannelsAsync_DoesNotLoopAfterSecondForbiddenResponse()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/channels/favorites",
            HttpStatusCode.OK,
            Fixture.Read("favorites.json"));
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/v3/cached/fixture-guide-hash/channels",
            HttpStatusCode.Forbidden,
            string.Empty);
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/v3/cached/fixture-guide-hash/channels",
            HttpStatusCode.Forbidden,
            string.Empty);

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);

        await Assert.ThrowsAsync<ZattooSessionExpiredException>(() => client.GetChannelsAsync());
        Assert.False(client.IsAuthenticated);
        Assert.Equal(2, transport.ResetCount);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task GetChannelsAsync_RejectsMalformedChannelDocument()
    {
        var transport = new FakeZattooTransport();
        ZattooClientAuthenticationTests.QueueAuthenticatedSession(transport);
        transport.Enqueue(HttpMethod.Get, "/zapi/channels/favorites", HttpStatusCode.OK, Fixture.Read("favorites.json"));
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/v3/cached/fixture-guide-hash/channels",
            HttpStatusCode.OK,
            "{\"groups\":[]}");

        using var client = ZattooClientAuthenticationTests.CreateClient(transport);

        await Assert.ThrowsAsync<ZattooProtocolException>(() => client.GetChannelsAsync());
        Assert.Equal(0, transport.PendingRequestCount);
    }

    private static void QueueChannels(FakeZattooTransport transport)
    {
        transport.Enqueue(HttpMethod.Get, "/zapi/channels/favorites", HttpStatusCode.OK, Fixture.Read("favorites.json"));
        transport.Enqueue(
            HttpMethod.Get,
            "/zapi/v3/cached/fixture-guide-hash/channels",
            HttpStatusCode.OK,
            Fixture.Read("channels.json"));
    }
}
