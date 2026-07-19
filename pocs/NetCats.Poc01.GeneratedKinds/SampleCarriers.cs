namespace NetCats.Poc01.GeneratedKinds;

[GenerateKind("LatentK")]
public readonly partial record struct Latent<A>(A Value);

[GenerateKind("EitherK", ValueParameter = 1)]
public readonly partial record struct Either<E, A>(A Value);

public sealed record ThirdPartyBox<A>(A Value);

[GenerateKind("ThirdPartyBoxK")]
public sealed partial class ThirdPartyBoxWrapper<A>
{
    private readonly ThirdPartyBox<A> inner;

    public ThirdPartyBoxWrapper(A value)
    {
        inner = new ThirdPartyBox<A>(value);
    }

    public A Value => inner.Value;

    public ThirdPartyBox<A> Unwrap() => inner;
}
