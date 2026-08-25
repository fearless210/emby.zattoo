using System.Net;
using System.Net.Http;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Core.Tests.TestInfrastructure;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooClientAuthenticationTests
{
    [Fact]
    public async Task LoginAsync_AuthenticatesAnonymousSession()
    {
        var transport = new FakeZattooTransport();
        QueueAnonymousLogin(transport);

        using var client = CreateClient(transport);
        await client.LoginAsync();

        Assert.True(client.IsAuthenticated);
        Assert.NotNull(client.SessionCreatedAt);
        Assert.Equal("CH", client.SessionInfo?.CountryCode);
        Assert.Equal("CH", client.SessionInfo?.ServiceCountry);
        Assert.Equal("fixture-guide-hash", client.SessionInfo?.PowerGuideHash);
        Assert.Equal(1, transport.ResetCount);
        Assert.Equal(0, transport.PendingRequestCount);

        var loginRequest = Assert.Single(
            transport.RecordedRequests,
            request => request.RelativePath.EndsWith("/account/login", StringComparison.Ordinal));
        Assert.Equal("fixture-user", loginRequest.Fields?["login"]);
        Assert.Equal("fixture-password", loginRequest.Fields?["password"]);
    }

    [Fact]
    public async Task LoginAsync_DoesNotSendCredentialsWhenSessionAlreadyHasAccount()
    {
        var transport = new FakeZattooTransport();
        QueueAuthenticatedSession(transport);

        using var client = CreateClient(transport);
        await client.LoginAsync();

        Assert.True(client.IsAuthenticated);
        Assert.DoesNotContain(
            transport.RecordedRequests,
            request => request.RelativePath.EndsWith("/account/login", StringComparison.Ordinal));
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task LoginAsync_RejectsInactiveHello()
    {
        var transport = new FakeZattooTransport();
        transport.Enqueue(HttpMethod.Get, "/token.json", HttpStatusCode.OK, Fixture.Read("token.json"));
        transport.Enqueue(HttpMethod.Post, "/zapi/v3/session/hello", HttpStatusCode.OK, "{\"active\":false}");

        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<ZattooAuthenticationException>(() => client.LoginAsync());
        Assert.False(client.IsAuthenticated);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task LoginAsync_FallsBackToApplicationBundleToken()
    {
        var transport = new FakeZattooTransport();
        transport.Enqueue(HttpMethod.Get, "/token.json", HttpStatusCode.NotFound, string.Empty);
        transport.Enqueue(
            HttpMethod.Get,
            "/login",
            HttpStatusCode.OK,
            "<html><script src=\"/app-fixture.js\"></script></html>");
        transport.Enqueue(
            HttpMethod.Get,
            "/app-fixture.js",
            HttpStatusCode.OK,
            "const tokenPath = \"token-fixture.json\";");
        transport.Enqueue(HttpMethod.Get, "/token-fixture.json", HttpStatusCode.OK, Fixture.Read("token.json"));
        transport.Enqueue(HttpMethod.Post, "/zapi/v3/session/hello", HttpStatusCode.OK, Fixture.Read("hello.json"));
        transport.Enqueue(HttpMethod.Get, "/zapi/v3/session", HttpStatusCode.OK, Fixture.Read("session-authenticated.json"));

        using var client = CreateClient(transport);
        await client.LoginAsync();

        var hello = Assert.Single(
            transport.RecordedRequests,
            request => request.RelativePath.EndsWith("/session/hello", StringComparison.Ordinal));
        Assert.Equal("fixture-app-token", hello.Fields?["client_app_token"]);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task LoginAsync_PropagatesCancellation()
    {
        var transport = new FakeZattooTransport();
        using var client = CreateClient(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.LoginAsync(cancellation.Token));
    }

    internal static ZattooClient CreateClient(FakeZattooTransport transport)
    {
        return new ZattooClient(
            new ZattooClientOptions
            {
                Username = "fixture-user",
                Password = "fixture-password",
                DeviceId = "fixture-device-id",
            },
            transport);
    }

    internal static void QueueAuthenticatedSession(FakeZattooTransport transport)
    {
        transport.Enqueue(HttpMethod.Get, "/token.json", HttpStatusCode.OK, Fixture.Read("token.json"));
        transport.Enqueue(HttpMethod.Post, "/zapi/v3/session/hello", HttpStatusCode.OK, Fixture.Read("hello.json"));
        transport.Enqueue(HttpMethod.Get, "/zapi/v3/session", HttpStatusCode.OK, Fixture.Read("session-authenticated.json"));
    }

    private static void QueueAnonymousLogin(FakeZattooTransport transport)
    {
        transport.Enqueue(HttpMethod.Get, "/token.json", HttpStatusCode.OK, Fixture.Read("token.json"));
        transport.Enqueue(HttpMethod.Post, "/zapi/v3/session/hello", HttpStatusCode.OK, Fixture.Read("hello.json"));
        transport.Enqueue(HttpMethod.Get, "/zapi/v3/session", HttpStatusCode.OK, Fixture.Read("session-anonymous.json"));
        transport.Enqueue(HttpMethod.Post, "/zapi/v3/account/login", HttpStatusCode.OK, Fixture.Read("login-success.json"));
    }
}
