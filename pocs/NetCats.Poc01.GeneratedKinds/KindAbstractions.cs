namespace NetCats.Poc01.GeneratedKinds;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class GenerateKindAttribute(string witnessName) : Attribute
{
    public string WitnessName { get; } = witnessName;

    public int ValueParameter { get; set; }
}

public readonly struct K<F, A>
{
    private readonly object value;

    public K(object value)
    {
        this.value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public TCarrier Get<TCarrier>() => value is TCarrier carrier
        ? carrier
        : throw new InvalidCastException($"The kind contains {value.GetType()}, not {typeof(TCarrier)}.");
}

public interface IFunctor<F>
{
    K<F, B> Map<A, B>(K<F, A> source, Func<A, B> selector);
}

public interface IMonad<F> : IFunctor<F>
{
    K<F, A> Pure<A>(A value);

    K<F, B> Bind<A, B>(K<F, A> source, Func<A, K<F, B>> selector);
}

public sealed record GeneratedLawRegistration(string WitnessName, object Instance);

public static class KindFunctions
{
    public static K<F, C> MapTwice<F, A, B, C>(
        IMonad<F> monad,
        K<F, A> source,
        Func<A, B> first,
        Func<B, C> second) => monad.Map(monad.Map(source, first), second);
}

public sealed record KindAllocationMeasurement(long StructCarrierBytes, long ClassCarrierBytes, int Iterations);

public static class KindAllocationProbe
{
    public static KindAllocationMeasurement Measure(int iterations = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        var beforeStruct = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            var kind = new K<LatentK, int>(new Latent<int>(index));
            GC.KeepAlive(kind.Get<Latent<int>>().Value);
        }

        var structBytes = GC.GetAllocatedBytesForCurrentThread() - beforeStruct;
        var beforeClass = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            var kind = new K<ThirdPartyBoxK, int>(new ThirdPartyBoxWrapper<int>(index));
            GC.KeepAlive(kind.Get<ThirdPartyBoxWrapper<int>>().Value);
        }

        var classBytes = GC.GetAllocatedBytesForCurrentThread() - beforeClass;
        return new KindAllocationMeasurement(structBytes, classBytes, iterations);
    }
}
