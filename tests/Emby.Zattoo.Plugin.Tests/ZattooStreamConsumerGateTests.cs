using Emby.Zattoo.Plugin.LiveTv;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooStreamConsumerGateTests
{
    private static readonly TimeSpan NoWait = TimeSpan.Zero;

    [Fact]
    public async Task TryEnterAsync_AdmitsTheFirstConsumer()
    {
        var gate = new ZattooStreamConsumerGate();

        Assert.True(await gate.TryEnterAsync(NoWait, CancellationToken.None));
    }

    [Fact]
    public async Task TryEnterAsync_AdmitsAConsumerAfterThePreviousOneLeft()
    {
        var gate = new ZattooStreamConsumerGate();
        Assert.True(await gate.TryEnterAsync(NoWait, CancellationToken.None));
        gate.Exit();

        // Emby detects the media of a live stream before transcoding it, so the
        // transcode attaches to the same pipe once detection is done.
        Assert.True(await gate.TryEnterAsync(NoWait, CancellationToken.None));
    }

    [Fact]
    public async Task TryEnterAsync_RefusesAConcurrentConsumerAfterTheHandoverTimeout()
    {
        var gate = new ZattooStreamConsumerGate();
        Assert.True(await gate.TryEnterAsync(NoWait, CancellationToken.None));

        Assert.False(await gate.TryEnterAsync(
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None));
    }

    [Fact]
    public async Task TryEnterAsync_WaitsForAConsumerLeavingDuringTheHandover()
    {
        var gate = new ZattooStreamConsumerGate();
        Assert.True(await gate.TryEnterAsync(NoWait, CancellationToken.None));

        var pending = gate.TryEnterAsync(
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        gate.Exit();

        Assert.True(await pending);
    }

    [Fact]
    public async Task TryEnterAsync_HonoursCancellationWhileWaiting()
    {
        var gate = new ZattooStreamConsumerGate();
        Assert.True(await gate.TryEnterAsync(NoWait, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();

        var pending = gate.TryEnterAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task TryEnterAsync_RejectsANegativeHandoverTimeout()
    {
        var gate = new ZattooStreamConsumerGate();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            gate.TryEnterAsync(TimeSpan.FromSeconds(-1), CancellationToken.None));
    }
}
