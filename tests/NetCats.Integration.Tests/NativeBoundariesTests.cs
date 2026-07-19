using System.Net;
using System.Net.Sockets;
using System.Text;
using NetCats.Core;
using NetCats.Reactive;
using NetCats.Runtime;

namespace NetCats.Integration.Tests;

public sealed class NativeBoundariesTests
{
    [Fact]
    public async Task Cold_effect_executes_real_loopback_http_only_when_run()
    {
        await using var server = new LoopbackHttpServer("netcats");
        using var client = new HttpClient();
        var effect = Latent<string>.DelayAsync(token => client.GetStringAsync(server.Uri, token));

        Assert.False(server.RequestReceived.Task.IsCompleted);
        var response = effect.RunAsync();
        await server.RequestReceived.Task;

        Assert.Equal("netcats", await response);
    }

    [Fact]
    public async Task Scope_shutdown_cancels_a_real_inflight_http_request_and_joins_the_fiber()
    {
        await using var server = new LoopbackHttpServer("late", holdResponse: true);
        using var client = new HttpClient();
        await using var scope = new FiberScope();
        var fiber = scope.Start(Latent<string>.DelayAsync(token => client.GetStringAsync(server.Uri, token)));
        await server.RequestReceived.Task;

        await scope.CloseAsync();

        Assert.IsType<Outcome<string>.Cancelled>(await fiber.JoinAsync());
        Assert.Equal(0, scope.ActiveChildCount);
    }

    [Fact]
    public async Task Protected_execution_flushes_and_releases_a_real_async_file_resource()
    {
        var path = Path.Combine(Path.GetTempPath(), "netcats-" + Guid.NewGuid().ToString("N") + ".txt");
        var payload = Encoding.UTF8.GetBytes("resource-safe async file I/O");
        try
        {
            var outcome = await ProtectedExecution.RunAsync<int>(async (_, finalizers) =>
            {
                var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous);
                finalizers.Register(_ => stream.DisposeAsync().AsTask());
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
                return payload.Length;
            });

            Assert.Equal(payload.Length, Assert.IsType<Outcome<int>.Succeeded>(outcome).Value);
            await using var check = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
            var read = new byte[payload.Length];
            Assert.Equal(payload.Length, await check.ReadAsync(read));
            Assert.Equal(payload, read);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task High_fan_out_workers_do_not_escape_scope_shutdown()
    {
        const int workerCount = 128;
        await using var scope = new FiberScope(ChildFailurePolicy.Ignore);
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => scope.Start(Latent<int>.DelayAsync(async token =>
            {
                if (Interlocked.Increment(ref started) == workerCount)
                {
                    allStarted.TrySetResult();
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            })))
            .ToArray();
        await allStarted.Task;

        await scope.CloseAsync();
        var outcomes = await Task.WhenAll(workers.Select(static worker => worker.JoinAsync()));

        Assert.All(outcomes, outcome => Assert.IsType<Outcome<int>.Cancelled>(outcome));
        Assert.Equal(0, scope.ActiveChildCount);
    }

    [Fact]
    public async Task Observable_disposal_requests_cancellation_of_an_http_backed_effect()
    {
        await using var server = new LoopbackHttpServer("late", holdResponse: true);
        using var client = new HttpClient();
        var observable = Latent<string>
            .DelayAsync(token => client.GetStringAsync(server.Uri, token))
            .ToObservable();
        var observer = new RecordingObserver<string>();
        var subscription = Assert.IsAssignableFrom<ICompletionSubscription>(observable.Subscribe(observer));
        await server.RequestReceived.Task;

        subscription.Dispose();
        await subscription.Completion;

        Assert.Empty(observer.Values);
        Assert.Null(observer.Error);
    }

    private sealed class RecordingObserver<T> : IObserver<T>
    {
        public List<T> Values { get; } = [];

        public Exception? Error { get; private set; }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => Error = error;

        public void OnNext(T value) => Values.Add(value);
    }

    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopped = new();
        private readonly Task responder;
        private readonly string body;
        private readonly bool holdResponse;

        public LoopbackHttpServer(string body, bool holdResponse = false)
        {
            this.body = body;
            this.holdResponse = holdResponse;
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Uri = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            responder = RespondAsync();
        }

        public Uri Uri { get; }

        public TaskCompletionSource RequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            stopped.Cancel();
            listener.Stop();
            try
            {
                await responder;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                stopped.Dispose();
            }
        }

        private async Task RespondAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(stopped.Token);
            await using var stream = client.GetStream();
            await ReadHeadersAsync(stream, stopped.Token);
            RequestReceived.TrySetResult();
            if (holdResponse)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, stopped.Token);
            await stream.WriteAsync(bytes, stopped.Token);
        }

        private static async Task ReadHeadersAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[1];
            var matched = 0;
            while (matched < 4)
            {
                var read = await stream.ReadAsync(buffer, token);
                if (read == 0)
                {
                    throw new IOException("The HTTP client disconnected before completing its request headers.");
                }

                matched = buffer[0] switch
                {
                    (byte)'\r' when matched is 0 or 2 => matched + 1,
                    (byte)'\n' when matched is 1 or 3 => matched + 1,
                    _ => 0,
                };
            }
        }
    }
}
