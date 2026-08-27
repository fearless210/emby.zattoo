using Emby.Zattoo.Plugin.LiveTv;
using Emby.Zattoo.Plugin.Tests.TestInfrastructure;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooRetiredClientQueueTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TakeExpired_KeepsClientsDuringTheGracePeriod()
    {
        var queue = new ZattooRetiredClientQueue(TimeSpan.FromMinutes(5));
        var client = new FakeZattooClient();
        queue.Retire(client, Now);

        Assert.Empty(queue.TakeExpired(Now.AddMinutes(4)));
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, client.DisposeCount);
    }

    [Fact]
    public void TakeExpired_ReleasesClientsOnceTheGracePeriodElapsed()
    {
        var queue = new ZattooRetiredClientQueue(TimeSpan.FromMinutes(5));
        var client = new FakeZattooClient();
        queue.Retire(client, Now);

        var expired = queue.TakeExpired(Now.AddMinutes(5));

        Assert.Same(client, Assert.Single(expired));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void TakeExpired_ReturnsEachClientOnlyOnce()
    {
        var queue = new ZattooRetiredClientQueue(TimeSpan.FromMinutes(5));
        queue.Retire(new FakeZattooClient(), Now);

        Assert.Single(queue.TakeExpired(Now.AddMinutes(6)));
        Assert.Empty(queue.TakeExpired(Now.AddMinutes(7)));
    }

    [Fact]
    public void TakeExpired_KeepsRecentClientsWhenReleasingOlderOnes()
    {
        var queue = new ZattooRetiredClientQueue(TimeSpan.FromMinutes(5));
        var older = new FakeZattooClient("older");
        var newer = new FakeZattooClient("newer");
        queue.Retire(older, Now);
        queue.Retire(newer, Now.AddMinutes(3));

        var expired = queue.TakeExpired(Now.AddMinutes(6));

        Assert.Same(older, Assert.Single(expired));
        Assert.Equal(1, queue.Count);
        Assert.Same(newer, Assert.Single(queue.TakeAll()));
    }

    [Fact]
    public void TakeAll_EmptiesTheQueueRegardlessOfTheGracePeriod()
    {
        var queue = new ZattooRetiredClientQueue(TimeSpan.FromMinutes(5));
        queue.Retire(new FakeZattooClient("first"), Now);
        queue.Retire(new FakeZattooClient("second"), Now);

        Assert.Equal(2, queue.TakeAll().Count);
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.TakeAll());
    }

    [Fact]
    public void Retire_RejectsMissingClient()
    {
        var queue = new ZattooRetiredClientQueue(TimeSpan.FromMinutes(5));

        Assert.Throws<ArgumentNullException>(() => queue.Retire(null!, Now));
    }
}
