using System.Net;
using System.Net.Http;
using Emby.Zattoo.Infrastructure;

namespace Emby.Zattoo.Core.Tests.TestInfrastructure;

internal sealed class FakeZattooTransport : IZattooTransport
{
    private readonly object syncRoot = new();
    private readonly Queue<ExpectedRequest> expectedRequests = new();
    private readonly List<RecordedRequest> recordedRequests = new();
    private bool disposed;

    public int ResetCount { get; private set; }

    public IReadOnlyList<RecordedRequest> RecordedRequests
    {
        get
        {
            lock (syncRoot)
            {
                return recordedRequests.ToArray();
            }
        }
    }

    public int PendingRequestCount
    {
        get
        {
            lock (syncRoot)
            {
                return expectedRequests.Count;
            }
        }
    }

    public void Enqueue(
        HttpMethod method,
        string relativePath,
        HttpStatusCode statusCode,
        string content)
    {
        lock (syncRoot)
        {
            expectedRequests.Enqueue(new ExpectedRequest(method, relativePath, statusCode, content));
        }
    }

    public TaskCompletionSource<bool> EnqueueDeferred(
        HttpMethod method,
        string relativePath,
        HttpStatusCode statusCode,
        string content)
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (syncRoot)
        {
            expectedRequests.Enqueue(
                new ExpectedRequest(method, relativePath, statusCode, content, release.Task));
        }

        return release;
    }

    public Task<ZattooTransportResponse> GetAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Get, relativePath, fields: null, cancellationToken);
    }

    public Task<ZattooTransportResponse> PostFormAsync(
        string relativePath,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Post, relativePath, fields, cancellationToken);
    }

    public void ResetSession(string deviceId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device ID is required.", nameof(deviceId));
        }

        ResetCount++;
    }

    public void Dispose()
    {
        disposed = true;
    }

    private Task<ZattooTransportResponse> SendAsync(
        HttpMethod method,
        string relativePath,
        IReadOnlyDictionary<string, string>? fields,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (expectedRequests.Count == 0)
            {
                throw new InvalidOperationException("No fake response was queued.");
            }

            var expected = expectedRequests.Dequeue();
            if (expected.Method != method || expected.RelativePath != relativePath)
            {
                throw new InvalidOperationException(
                    $"Expected {expected.Method} {expected.RelativePath}, got {method} {relativePath}.");
            }

            recordedRequests.Add(new RecordedRequest(
                method,
                relativePath,
                fields == null
                    ? null
                    : new Dictionary<string, string>(fields, StringComparer.Ordinal)));

            return expected.Release == null
                ? Task.FromResult(
                    new ZattooTransportResponse(expected.StatusCode, expected.Content))
                : CompleteDeferredAsync(expected, cancellationToken);
        }
    }

    private static async Task<ZattooTransportResponse> CompleteDeferredAsync(
        ExpectedRequest expected,
        CancellationToken cancellationToken)
    {
        await expected.Release!.WaitAsync(cancellationToken);
        return new ZattooTransportResponse(expected.StatusCode, expected.Content);
    }

    private sealed class ExpectedRequest
    {
        public ExpectedRequest(
            HttpMethod method,
            string relativePath,
            HttpStatusCode statusCode,
            string content,
            Task? release = null)
        {
            Method = method;
            RelativePath = relativePath;
            StatusCode = statusCode;
            Content = content;
            Release = release;
        }

        public HttpMethod Method { get; }

        public string RelativePath { get; }

        public HttpStatusCode StatusCode { get; }

        public string Content { get; }

        public Task? Release { get; }
    }
}
internal sealed class RecordedRequest
{
    public RecordedRequest(
        HttpMethod method,
        string relativePath,
        IReadOnlyDictionary<string, string>? fields)
    {
        Method = method;
        RelativePath = relativePath;
        Fields = fields;
    }

    public HttpMethod Method { get; }

    public string RelativePath { get; }

    public IReadOnlyDictionary<string, string>? Fields { get; }
}
