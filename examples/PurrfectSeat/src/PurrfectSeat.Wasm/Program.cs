using System.Runtime.Versioning;
using PurrfectSeat.Application;
using PurrfectSeat.Infrastructure;
using PurrfectSeat.Wasm.Services;

namespace PurrfectSeat.Wasm;

[SupportedOSPlatform("browser")]
internal static class Program
{
    public static void Main()
    {
        var repository = new InMemoryPerformanceRepository();
        foreach (var performance in SeedData.CreateCatalogue())
        {
            repository.Seed(performance);
        }

        var projection = new DemoProjection();
        var application = new BookingApplication(
            repository,
            repository,
            new AlwaysEligibleCustomers(),
            new FixedPricing(),
            new SimulatedPayments(),
            new SystemClock(),
            projection);
        WasmBookingBridge.Initialize(new BrowserBookingApi(application, repository));
    }
}
