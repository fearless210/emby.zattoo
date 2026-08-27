using Emby.Zattoo.Models;
using Emby.Zattoo.Zattoo;
using Emby.Zattoo.Exceptions;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooGuideDetailsServiceTests
{
    [Fact]
    public async Task QueuePrograms_PrioritizesNextThenFavoritesAndUpcomingPrograms()
    {
        var now = DateTimeOffset.UtcNow;
        var requested = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ZattooGuideDetailsService(
            TimeSpan.Zero,
            TimeSpan.Zero,
            (ids, _) =>
            {
                requested.TrySetResult(ids);
                return Task.FromResult<IReadOnlyList<ZattooProgramDetails>>(
                    Array.Empty<ZattooProgramDetails>());
            },
            progress =>
            {
                if (progress.Kind == ZattooGuideDetailsProgressKind.Completed)
                {
                    completed.TrySetResult(true);
                }
            });

        service.QueuePrograms(
            new[]
            {
                CreateProgram("later", "regular", now.AddHours(2)),
                CreateProgram("favorite", "favorite-channel", now.AddHours(3)),
                CreateProgram("sooner", "regular", now.AddHours(1)),
                new ZattooProgram
                {
                    Id = "already-rich",
                    ChannelId = "favorite-channel",
                    Name = "Complete fixture",
                    Overview = "Already available.",
                    Genres = new[] { "Magazine" },
                    StartDate = now.AddMinutes(30),
                    EndDate = now.AddMinutes(45),
                },
            },
            new[] { "favorite-channel" },
            now,
            now.AddHours(5));

        var ids = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "sooner", "favorite", "later" }, ids);
    }

    [Fact]
    public async Task QueuePrograms_SkipsDistantNonFavoritePrograms()
    {
        var now = DateTimeOffset.UtcNow;
        var requested = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ZattooGuideDetailsService(
            TimeSpan.Zero,
            TimeSpan.Zero,
            (ids, _) =>
            {
                requested.TrySetResult(ids);
                return Task.FromResult<IReadOnlyList<ZattooProgramDetails>>(
                    Array.Empty<ZattooProgramDetails>());
            },
            progress =>
            {
                if (progress.Kind == ZattooGuideDetailsProgressKind.Completed)
                {
                    completed.TrySetResult(true);
                }
            });

        service.QueuePrograms(
            new[]
            {
                CreateProgram("near", "regular", now.AddHours(23)),
                CreateProgram("distant", "regular", now.AddHours(48)),
                CreateProgram("favorite", "favorite-channel", now.AddHours(72)),
            },
            new[] { "favorite-channel" },
            now,
            now.AddDays(7));

        var ids = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "favorite", "near" }, ids);
        Assert.DoesNotContain("distant", ids);
    }

    [Fact]
    public async Task PersistentCache_SkipsUnchangedProgramsAndRefetchesChangedPrograms()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "emby-zattoo-guide-cache-tests",
            Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(folder, "details.jsonl");
        var now = DateTimeOffset.UtcNow;
        var program = CreateProgram("persistent", "regular", now.AddHours(1));
        try
        {
            var firstCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (var first = new ZattooGuideDetailsService(
                TimeSpan.Zero,
                TimeSpan.Zero,
                (ids, _) => Task.FromResult<IReadOnlyList<ZattooProgramDetails>>(
                    new[]
                    {
                        new ZattooProgramDetails
                        {
                            Id = ids[0],
                            Overview = "Persisted fixture detail.",
                        },
                    }),
                progress =>
                {
                    if (progress.Kind == ZattooGuideDetailsProgressKind.Completed)
                    {
                        firstCompleted.TrySetResult(true);
                    }
                },
                cachePath,
                "fixture-scope"))
            {
                first.QueuePrograms(
                    new[] { program },
                    Array.Empty<string>(),
                    now,
                    now.AddHours(2));
                await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            var secondRequests = 0;
            var secondCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (var second = new ZattooGuideDetailsService(
                TimeSpan.Zero,
                TimeSpan.Zero,
                (ids, _) =>
                {
                    Interlocked.Increment(ref secondRequests);
                    return Task.FromResult<IReadOnlyList<ZattooProgramDetails>>(
                        new[]
                        {
                            new ZattooProgramDetails
                            {
                                Id = ids[0],
                                Overview = "Updated fixture detail.",
                            },
                        });
                },
                progress =>
                {
                    if (progress.Kind == ZattooGuideDetailsProgressKind.Completed)
                    {
                        secondCompleted.TrySetResult(true);
                    }
                },
                cachePath,
                "fixture-scope"))
            {
                var restoredForDisplay = CreateProgram(
                    "persistent",
                    "regular",
                    now.AddHours(1));
                second.ApplyDetails(restoredForDisplay);
                Assert.Equal(
                    "Persisted fixture detail.",
                    restoredForDisplay.Overview);

                var unchanged = CreateProgram(
                    "persistent",
                    "regular",
                    now.AddHours(1));
                second.QueuePrograms(
                    new[] { unchanged },
                    Array.Empty<string>(),
                    now,
                    now.AddHours(2));
                Assert.Equal(0, Volatile.Read(ref secondRequests));

                var changed = CreateProgram(
                    "persistent",
                    "regular",
                    now.AddHours(1));
                changed.Name = "Changed fixture program";
                second.QueuePrograms(
                    new[] { changed },
                    Array.Empty<string>(),
                    now,
                    now.AddHours(2));
                await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, Volatile.Read(ref secondRequests));
            }
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder);
            }
        }
    }

    [Fact]
    public async Task PersistentCache_DoesNotReuseEntriesFromAnotherScope()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "emby-zattoo-guide-cache-tests",
            Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(folder, "details.jsonl");
        var now = DateTimeOffset.UtcNow;
        var program = CreateProgram("scoped", "regular", now.AddHours(1));
        try
        {
            var firstCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (var first = new ZattooGuideDetailsService(
                TimeSpan.Zero,
                TimeSpan.Zero,
                (ids, _) => Task.FromResult<IReadOnlyList<ZattooProgramDetails>>(
                    new[]
                    {
                        new ZattooProgramDetails
                        {
                            Id = ids[0],
                            Overview = "First scope fixture.",
                        },
                    }),
                progress =>
                {
                    if (progress.Kind == ZattooGuideDetailsProgressKind.Completed)
                    {
                        firstCompleted.TrySetResult(true);
                    }
                },
                cachePath,
                "first-scope"))
            {
                first.QueuePrograms(
                    new[] { program },
                    Array.Empty<string>(),
                    now,
                    now.AddHours(2));
                await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            var secondCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRequests = 0;
            using (var second = new ZattooGuideDetailsService(
                TimeSpan.Zero,
                TimeSpan.Zero,
                (ids, _) =>
                {
                    Interlocked.Increment(ref secondRequests);
                    return Task.FromResult<IReadOnlyList<ZattooProgramDetails>>(
                        Array.Empty<ZattooProgramDetails>());
                },
                progress =>
                {
                    if (progress.Kind == ZattooGuideDetailsProgressKind.Completed)
                    {
                        secondCompleted.TrySetResult(true);
                    }
                },
                cachePath,
                "second-scope"))
            {
                second.QueuePrograms(
                    new[] { program },
                    Array.Empty<string>(),
                    now,
                    now.AddHours(2));
                await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, Volatile.Read(ref secondRequests));
            }
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder);
            }
        }
    }

    [Fact]
    public async Task PrioritizePrograms_MovesStreamProgramAheadOfPendingBackgroundWork()
    {
        var now = DateTimeOffset.UtcNow;
        var firstBatchStarted = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBatchStarted = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstBatch = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var service = new ZattooGuideDetailsService(
            TimeSpan.Zero,
            TimeSpan.Zero,
            async (ids, cancellationToken) =>
            {
                var currentRequest = Interlocked.Increment(ref requestCount);
                if (currentRequest == 1)
                {
                    firstBatchStarted.TrySetResult(ids);
                    await releaseFirstBatch.Task.WaitAsync(cancellationToken);
                }
                else if (currentRequest == 2)
                {
                    secondBatchStarted.TrySetResult(ids);
                }

                return Array.Empty<ZattooProgramDetails>();
            },
            null);
        var programs = Enumerable.Range(0, 42)
            .Select(index => CreateProgram(
                "program-" + index.ToString("D2"),
                "regular",
                now.AddHours(4).AddMinutes(index)))
            .ToArray();
        service.QueuePrograms(
            programs,
            Array.Empty<string>(),
            now,
            now.AddHours(6));
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.PrioritizePrograms(new[] { programs[41] });
        releaseFirstBatch.TrySetResult(true);
        var secondBatch = await secondBatchStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.Equal("program-41", secondBatch[0]);
        service.Stop();
    }

    [Fact]
    public async Task QueuePrograms_CachesNegativeResultsPurgesExpiredAndFetchesOnlyNewIds()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var requests = new List<IReadOnlyList<string>>();
        var completions = new SemaphoreSlim(0);
        ZattooGuideDetailsProgress? lastCompletion = null;
        using var service = new ZattooGuideDetailsService(
            TimeSpan.Zero,
            TimeSpan.Zero,
            (ids, _) =>
            {
                lock (requests)
                {
                    requests.Add(ids.ToArray());
                }

                return Task.FromResult<IReadOnlyList<ZattooProgramDetails>>(
                    ids.Where(id => id != "without-details")
                        .Select(id => new ZattooProgramDetails
                        {
                            Id = id,
                            Overview = "Detailed overview for " + id,
                        })
                        .ToArray());
            },
            progress =>
            {
                if (progress.Kind == ZattooGuideDetailsProgressKind.Completed)
                {
                    lastCompletion = progress;
                    completions.Release();
                }
            },
            TimeSpan.Zero,
            TimeSpan.Zero,
            () => clock);
        var first = CreateProgram("first", "regular", now.AddHours(1));
        var withoutDetails = CreateProgram(
            "without-details",
            "regular",
            now.AddHours(1));

        service.QueuePrograms(
            new[] { first, withoutDetails },
            Array.Empty<string>(),
            now,
            now.AddHours(2));
        Assert.True(await completions.WaitAsync(TimeSpan.FromSeconds(5)));

        var enriched = CreateProgram("first", "regular", now.AddHours(1));
        service.ApplyDetails(enriched);
        Assert.Equal("Detailed overview for first", enriched.Overview);
        service.ApplyDetails(withoutDetails);
        Assert.Null(withoutDetails.Overview);

        service.QueuePrograms(
            new[] { first, withoutDetails },
            Array.Empty<string>(),
            now,
            now.AddHours(2));
        lock (requests)
        {
            Assert.Single(requests);
        }

        clock = now.AddHours(2);
        var newProgram = CreateProgram("new", "regular", clock.AddHours(1));
        service.QueuePrograms(
            new[] { first, withoutDetails, newProgram },
            Array.Empty<string>(),
            clock,
            clock.AddHours(2));
        Assert.True(await completions.WaitAsync(TimeSpan.FromSeconds(5)));

        lock (requests)
        {
            Assert.Equal(2, requests.Count);
            Assert.Equal(new[] { "new" }, requests[1]);
        }

        Assert.NotNull(lastCompletion);
        Assert.True(lastCompletion!.RemovedPrograms >= 2);
        Assert.Equal(1, lastCompletion.CachedPrograms);
    }

    [Fact]
    public async Task Stop_CancelsAnActiveRequestAndRejectsAdditionalWork()
    {
        var now = DateTimeOffset.UtcNow;
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var service = new ZattooGuideDetailsService(
            TimeSpan.Zero,
            TimeSpan.Zero,
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref requestCount);
                requestStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Array.Empty<ZattooProgramDetails>();
            },
            progress =>
            {
                if (progress.Kind == ZattooGuideDetailsProgressKind.Stopped)
                {
                    stopped.TrySetResult(true);
                }
            });
        var program = CreateProgram("active", "regular", now.AddHours(1));
        service.QueuePrograms(
            new[] { program },
            Array.Empty<string>(),
            now,
            now.AddHours(2));
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.Stop();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.QueuePrograms(
            new[] { CreateProgram("ignored", "regular", now.AddHours(1)) },
            Array.Empty<string>(),
            now,
            now.AddHours(2));

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task QueuePrograms_RetriesTransientFailureWithoutLosingTheBatch()
    {
        var now = DateTimeOffset.UtcNow;
        var completed = new TaskCompletionSource<ZattooGuideDetailsProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retryReported = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var service = new ZattooGuideDetailsService(
            TimeSpan.Zero,
            TimeSpan.Zero,
            (ids, _) =>
            {
                if (Interlocked.Increment(ref requestCount) == 1)
                {
                    throw new ZattooTransportException("Transient fixture failure.");
                }

                return Task.FromResult<IReadOnlyList<ZattooProgramDetails>>(
                    new[]
                    {
                        new ZattooProgramDetails
                        {
                            Id = ids[0],
                            Overview = "Recovered fixture detail.",
                        },
                    });
            },
            progress =>
            {
                if (progress.Kind == ZattooGuideDetailsProgressKind.Retrying)
                {
                    retryReported.TrySetResult(true);
                }

                if (progress.Kind == ZattooGuideDetailsProgressKind.Completed)
                {
                    completed.TrySetResult(progress);
                }
            });
        service.QueuePrograms(
            new[] { CreateProgram("retry", "regular", now.AddHours(1)) },
            Array.Empty<string>(),
            now,
            now.AddHours(2));

        await retryReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, requestCount);
        Assert.Equal(1, result.FailedBatches);
        Assert.Equal(1, result.CachedPrograms);
    }

    private static ZattooProgram CreateProgram(
        string id,
        string channelId,
        DateTimeOffset startDate)
    {
        return new ZattooProgram
        {
            Id = id,
            ChannelId = channelId,
            Name = "Fixture program",
            StartDate = startDate,
            EndDate = startDate.AddMinutes(30),
        };
    }
}
