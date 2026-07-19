using NetCats.Core;

namespace NetCats.Core.Tests;

public sealed class LatentTests
{
    [Fact]
    public async Task Construction_is_cold_and_linq_and_direct_bind_are_equivalent()
    {
        var executions = 0;
        var source = Latent<int>.Delay(() => ++executions);
        var direct = source.Bind(value => Latent<int>.Pure(value + 10));
        var query =
            from value in source
            from increment in Latent<int>.Pure(10)
            select value + increment;

        Assert.Equal(0, executions);
        Assert.Equal(11, await direct.RunAsync());
        Assert.Equal(12, await query.RunAsync());
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task Interpreter_handles_deep_composition_without_consuming_the_call_stack()
    {
        var effect = Latent<int>.Pure(0);
        for (var index = 0; index < 100_000; index++)
        {
            effect = effect.Bind(static value => Latent<int>.Pure(value + 1));
        }

        Assert.Equal(100_000, await effect.RunAsync());
    }

    [Fact]
    public async Task Factories_capture_synchronous_errors_and_recovery_is_explicit()
    {
        var effect = Latent<int>.Delay(() => throw new InvalidOperationException("expected"));

        Assert.Equal(42, await effect.Recover(_ => 42).RunAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => effect.RunAsync());
    }
}
