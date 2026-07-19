using NetCats.Poc09.Performance;

namespace NetCats.Pocs.Tests;

public sealed class Poc09PerformanceTests
{
    [Fact]
    public async Task Representative_probe_quantifies_each_feature_separately()
    {
        var measurements = await PerformanceProbe.RunRepresentativeAsync(bindDepth: 1_000, fanOut: 8);

        Assert.Equal(8, measurements.Count);
        Assert.Equal(8, measurements.Select(static measurement => measurement.Name).Distinct().Count());
        Assert.All(measurements, measurement =>
        {
            Assert.True(measurement.Elapsed >= TimeSpan.Zero);
            Assert.True(measurement.AllocatedBytes >= 0);
            Assert.True(measurement.Operations > 0);
        });
    }

    [Fact]
    public void Deployment_probe_records_runtime_mode_for_jit_aot_comparison()
    {
        var capabilities = PerformanceProbe.GetDeploymentCapabilities();

        Assert.False(string.IsNullOrWhiteSpace(capabilities.FrameworkDescription));
        Assert.Equal(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported, capabilities.IsDynamicCodeSupported);
    }
}
