using PurrfectSeat.Api;
using PurrfectSeat.Application;
using PurrfectSeat.Infrastructure;
using PurrfectSeat.Scenarios;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<InMemoryPerformanceRepository>();
builder.Services.AddSingleton<IBookingUnitOfWorkFactory>(static provider => provider.GetRequiredService<InMemoryPerformanceRepository>());
builder.Services.AddSingleton<IPerformanceReader>(static provider => provider.GetRequiredService<InMemoryPerformanceRepository>());
builder.Services.AddSingleton<ICustomerService, AlwaysEligibleCustomers>();
builder.Services.AddSingleton<IPricingService, FixedPricing>();
builder.Services.AddSingleton<IPaymentGateway, SimulatedPayments>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<DemoProjection>();
builder.Services.AddSingleton<IApplicationEventSink>(static provider => provider.GetRequiredService<DemoProjection>());
builder.Services.AddSingleton<BookingApplication>();
builder.Services.AddSingleton<ScenarioRunner>();
builder.Services.AddHostedService<ExpiryWorker>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

var repository = app.Services.GetRequiredService<InMemoryPerformanceRepository>();
foreach (var performance in SeedData.CreateCatalogue())
{
    repository.Seed(performance);
}

app.MapGet("/performances", PurrfectSeatEndpoints.ListPerformances);
app.MapGet("/control-room", () => TypedResults.Redirect("/control-room.html"));
var performances = app.MapGroup("/performances/{performanceId:guid}");
performances.MapPost("/holds", PurrfectSeatEndpoints.HoldSeats);
performances.MapPost("/holds/{holdId:guid}/confirm", PurrfectSeatEndpoints.ConfirmHold);
performances.MapDelete("/holds/{holdId:guid}", PurrfectSeatEndpoints.CancelHold);
performances.MapGet("/holds/{holdId:guid}", PurrfectSeatEndpoints.GetHold);
performances.MapGet("/availability", PurrfectSeatEndpoints.GetAvailability);
performances.MapGet("/availability/stream", PurrfectSeatEndpoints.StreamAvailability);

if (app.Environment.IsEnvironment("Demo"))
{
    app.MapGet("/demo/scenarios", PurrfectSeatEndpoints.ListScenarios);
    app.MapPost("/demo/scenarios/{name}/start", PurrfectSeatEndpoints.StartScenario);
    app.MapGet("/demo/telemetry/snapshot", PurrfectSeatEndpoints.TelemetrySnapshot);
    app.MapGet("/demo/telemetry/stream", PurrfectSeatEndpoints.StreamTelemetry);
}

app.Run();

public partial class Program;
