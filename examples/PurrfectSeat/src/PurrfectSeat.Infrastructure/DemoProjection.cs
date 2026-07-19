using System.Collections.Concurrent;
using System.Threading.Channels;
using PurrfectSeat.Application;
using PurrfectSeat.Contracts;
using PurrfectSeat.Domain;

namespace PurrfectSeat.Infrastructure;

public sealed class DemoProjection : IApplicationEventSink
{
    private readonly object gate = new();
    private readonly List<TelemetryEvent> feed = [];
    private readonly BroadcastHub<AvailabilityEvent> availability = new();
    private readonly BroadcastHub<TelemetryEvent> telemetry = new();
    private long cursor;
    private int holdsCreated;
    private int holdsConfirmed;
    private int holdsExpired;

    public Task PublishAsync(IReadOnlyCollection<DomainEvent> events, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var domainEvent in events)
        {
            var id = Interlocked.Increment(ref cursor);
            var availabilityEvent = new AvailabilityEvent(id, domainEvent.PerformanceId.Value, 0, domainEvent.GetType().Name, domainEvent.OccurredAt);
            availability.Publish(availabilityEvent);
            var message = domainEvent switch
            {
                SeatsHeld held => $"Hold {held.HoldId.Value.ToString("N")[..6]} placed for {string.Join(',', held.Seats)}.",
                HoldConfirmed confirmed => $"Hold {confirmed.HoldId.Value.ToString("N")[..6]} confirmed.",
                HoldCancelled cancelled => $"Hold {cancelled.HoldId.Value.ToString("N")[..6]} cancelled.",
                HoldExpired expired => $"Hold {expired.HoldId.Value.ToString("N")[..6]} expired.",
                _ => "Domain activity committed.",
            };
            var eventItem = new TelemetryEvent(id, "domain", message, domainEvent.OccurredAt);
            lock (gate)
            {
                feed.Add(eventItem);
                if (feed.Count > 100)
                {
                    feed.RemoveAt(0);
                }
            }

            telemetry.Publish(eventItem);
            switch (domainEvent)
            {
                case SeatsHeld:
                    Interlocked.Increment(ref holdsCreated);
                    break;
                case HoldConfirmed:
                    Interlocked.Increment(ref holdsConfirmed);
                    break;
                case HoldExpired:
                    Interlocked.Increment(ref holdsExpired);
                    break;
            }
        }

        return Task.CompletedTask;
    }

    public TelemetrySnapshot Snapshot(int activeFibers = 0) 
    {
        lock (gate)
        {
            return new TelemetrySnapshot(
                Volatile.Read(ref cursor),
                activeFibers,
                0,
                0,
                Volatile.Read(ref holdsCreated),
                Volatile.Read(ref holdsConfirmed),
                Volatile.Read(ref holdsExpired),
                0,
                feed.ToArray());
        }
    }

    public IAsyncEnumerable<AvailabilityEvent> AvailabilityStream(CancellationToken cancellationToken) => availability.Subscribe(cancellationToken);

    public IAsyncEnumerable<TelemetryEvent> TelemetryStream(CancellationToken cancellationToken) => telemetry.Subscribe(cancellationToken);

    private sealed class BroadcastHub<T>
    {
        private readonly ConcurrentDictionary<Guid, Channel<T>> subscribers = [];

        public void Publish(T item)
        {
            foreach (var subscriber in subscribers.Values)
            {
                subscriber.Writer.TryWrite(item);
            }
        }

        public async IAsyncEnumerable<T> Subscribe([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
            subscribers.TryAdd(id, channel);
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return item;
                }
            }
            finally
            {
                subscribers.TryRemove(id, out _);
                channel.Writer.TryComplete();
            }
        }
    }
}
