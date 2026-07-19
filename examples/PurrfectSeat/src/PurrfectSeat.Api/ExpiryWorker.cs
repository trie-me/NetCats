using NetCats.Core;
using NetCats.Runtime;
using PurrfectSeat.Application;

namespace PurrfectSeat.Api;

public sealed class ExpiryWorker(BookingApplication application, ILogger<ExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = new FiberScope();
        var fiber = scope.Start(Latent<int>.DelayAsync(async token =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                await application.ExpireDueHolds().RunAsync(token).ConfigureAwait(false);
            }
#pragma warning disable CS0162
            return 0;
#pragma warning restore CS0162
        }));

        using var registration = stoppingToken.Register(() => fiber.RequestCancellation());
        await fiber.JoinAsync().ConfigureAwait(false);
        await scope.CloseAsync().ConfigureAwait(false);
        logger.LogInformation("Expiry worker drained all owned fibers.");
    }
}
