using NetCats.Poc01.GeneratedKinds;
using NetCats.Poc09.Performance;

var environment = PerformanceProbe.GetDeploymentCapabilities();
var allocations = KindAllocationProbe.Measure();
var measurements = await PerformanceProbe.RunRepresentativeAsync();

Console.WriteLine($"Runtime: {environment.FrameworkDescription}");
Console.WriteLine($"Dynamic code supported/compiled: {environment.IsDynamicCodeSupported}/{environment.IsDynamicCodeCompiled}");
Console.WriteLine($"Kind carrier allocations ({allocations.Iterations} iterations): struct={allocations.StructCarrierBytes} B, class={allocations.ClassCarrierBytes} B");
Console.WriteLine("Measurement | Operations | Elapsed | Allocated | ns/op | B/op");
foreach (var measurement in measurements)
{
    Console.WriteLine(
        $"{measurement.Name} | {measurement.Operations} | {measurement.Elapsed.TotalMilliseconds:F3} ms | " +
        $"{measurement.AllocatedBytes} B | {measurement.NanosecondsPerOperation:F2} | {measurement.BytesPerOperation:F2}");
}
