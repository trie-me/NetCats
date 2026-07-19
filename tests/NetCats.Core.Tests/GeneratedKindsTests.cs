using NetCats.Core;

namespace NetCats.Core.Tests;

public sealed class GeneratedKindsTests
{
    [Fact]
    public void Generated_witness_conversion_and_syntax_are_available_to_consumers()
    {
        var mapped = new Box<int>(20).Map(static value => value + 1);
        var kind = mapped.ToKind();

        Assert.Equal(21, mapped.Value);
        Assert.Equal(mapped, kind.FromKind());
    }
}

[GenerateKind("BoxK")]
public readonly partial record struct Box<A>(A Value);
