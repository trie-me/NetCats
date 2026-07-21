using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetCats.Runtime;
using NetCats.Testing;

namespace NetCats.AspNetCore.Tests;

public sealed class FiberDiagnosticsStreamTests
{
    [Fact]
    public async Task Burst_changes_coalesce_to_the_latest_complete_snapshot()
    {
        var clock = new SignalingTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new FiberDiagnosticsOptions
        {
            MaximumNodes = 32,
            MaximumPublishRate = TimeSpan.FromSeconds(1),
            CompletedRetention = TimeSpan.FromHours(1),
        });
        var registry = new FiberDiagnosticsRegistry(options, clock);
        var scopeId = Guid.NewGuid();
        registry.Observe(FiberDiagnosticsRegistryTests.ScopeOpened(scopeId, clock.GetUtcNow()));
        using var shutdown = new CancellationTokenSource();
        var body = new RecordingStream();
        var context = CreateContext(body, shutdown.Token);
        var streaming = FiberDiagnosticsEndpointRouteBuilderExtensions.StreamSnapshots(
            context, registry, options, clock, new TestApplicationLifetime());
        await body.WaitForWritesAsync(1);
        await clock.WaitForTimerAsync();

        for (var index = 0; index < 500; index++)
        {
            registry.Observe(FiberDiagnosticsRegistryTests.FiberStarted(scopeId, Guid.NewGuid(), $"burst-{index}", clock.GetUtcNow()));
        }
        var latestVersion = registry.GetSnapshot().Version;
        clock.Advance(TimeSpan.FromSeconds(1));
        await body.WaitForWritesAsync(2);

        var snapshots = ReadSnapshots(body.Text);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(1, snapshots[0].GetProperty("version").GetInt64());
        Assert.Equal(latestVersion, snapshots[1].GetProperty("version").GetInt64());
        Assert.True(snapshots[1].GetProperty("isTruncated").GetBoolean());
        Assert.Equal("Open", snapshots[1].GetProperty("roots")[0].GetProperty("state").GetString());
        Assert.Equal("Running", snapshots[1].GetProperty("roots")[0].GetProperty("fibers")[0].GetProperty("state").GetString());

        shutdown.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_blocked_client_does_not_backpressure_observation_and_cancels_cleanly_on_shutdown()
    {
        var time = TimeProvider.System;
        var options = Options.Create(new FiberDiagnosticsOptions
        {
            MaximumNodes = 64,
            MaximumPublishRate = TimeSpan.FromSeconds(1),
            CompletedRetention = TimeSpan.FromMinutes(1),
        });
        var registry = new FiberDiagnosticsRegistry(options, time);
        var scopeId = Guid.NewGuid();
        registry.Observe(FiberDiagnosticsRegistryTests.ScopeOpened(scopeId, time.GetUtcNow()));
        using var shutdown = new CancellationTokenSource();
        var body = new BlockingStream();
        var context = CreateContext(body, shutdown.Token);
        var streaming = FiberDiagnosticsEndpointRouteBuilderExtensions.StreamSnapshots(
            context, registry, options, time, new TestApplicationLifetime());
        await body.WriteEntered;

        await Task.Run(() => Parallel.For(0, 2_000, index =>
        {
            var fiberId = Guid.NewGuid();
            registry.Observe(FiberDiagnosticsRegistryTests.FiberStarted(scopeId, fiberId, $"blocked-{index}", time.GetUtcNow()));
            registry.Observe(FiberDiagnosticsRegistryTests.FiberTerminated(scopeId, fiberId, $"blocked-{index}", time.GetUtcNow()));
        })).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4_001, registry.GetSnapshot().Version);
        shutdown.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(body.CancellationObserved);
    }

    [Fact]
    public async Task All_connected_streams_release_promptly_when_the_host_stops()
    {
        var time = TimeProvider.System;
        var options = Options.Create(new FiberDiagnosticsOptions { MaximumPublishRate = TimeSpan.FromMinutes(1) });
        var registry = new FiberDiagnosticsRegistry(options, time);
        registry.Observe(FiberDiagnosticsRegistryTests.ScopeOpened(Guid.NewGuid(), time.GetUtcNow()));
        var lifetime = new TestApplicationLifetime();
        var bodies = Enumerable.Range(0, 32).Select(static _ => new RecordingStream()).ToArray();
        var streams = bodies.Select(body => FiberDiagnosticsEndpointRouteBuilderExtensions.StreamSnapshots(
            CreateContext(body, CancellationToken.None), registry, options, time, lifetime)).ToArray();
        await Task.WhenAll(bodies.Select(static body => body.WaitForWritesAsync(1)));

        lifetime.StopApplication();

        await Task.WhenAll(streams).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(streams, static stream => Assert.True(stream.IsCompletedSuccessfully));
    }

    private static DefaultHttpContext CreateContext(Stream body, CancellationToken requestAborted)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        context.RequestAborted = requestAborted;
        return context;
    }

    private static List<JsonElement> ReadSnapshots(string events) => events
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(static line => line.StartsWith("data: ", StringComparison.Ordinal))
        .Select(static line => JsonDocument.Parse(line[6..]).RootElement.Clone())
        .ToList();

    private sealed class RecordingStream : Stream
    {
        private readonly object gate = new();
        private readonly MemoryStream inner = new();
        private readonly List<TaskCompletionSource> writeWaiters = [];
        private int writes;

        public string Text
        {
            get
            {
                lock (gate)
                {
                    return Encoding.UTF8.GetString(inner.ToArray());
                }
            }
        }

        public Task WaitForWritesAsync(int count)
        {
            lock (gate)
            {
                if (writes >= count)
                {
                    return Task.CompletedTask;
                }

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                while (writeWaiters.Count < count)
                {
                    writeWaiters.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }
                writeWaiters[count - 1] = waiter;
                return waiter.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                inner.Write(buffer.Span);
                writes++;
                if (writeWaiters.Count >= writes)
                {
                    writeWaiters[writes - 1].TrySetResult();
                }
            }
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource writeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteEntered => writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public bool CancellationObserved { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            writeEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class SignalingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly ManualTimeProvider inner = new(start);
        private readonly TaskCompletionSource timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long TimestampFrequency => inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            timerCreated.TrySetResult();
            return timer;
        }

        public Task WaitForTimerAsync() => timerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Advance(TimeSpan amount) => inner.Advance(amount);
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() => stopping.Cancel();
    }
}
