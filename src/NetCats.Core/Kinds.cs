namespace NetCats.Core;

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

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class GenerateKindAttribute(string witnessName) : Attribute
{
    public string WitnessName { get; } = witnessName;

    public int ValueParameter { get; set; }
}
