using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetCats.Poc01.GeneratedKinds;
using NetCats.Poc01.Generator;

namespace NetCats.Pocs.Tests;

public sealed class Poc01GeneratedKindsTests
{
    [Fact]
    public void Generated_surfaces_work_for_unary_binary_and_wrapped_carriers()
    {
        var direct = new Latent<int>(20).Map(static value => value + 1);
        var query =
            from left in new Latent<int>(20)
            from right in new Latent<int>(1)
            select left + right;
        var either = new Either<string, int>(7).Bind(static value => new Either<string, int>(value * 3));
        var wrapped = new ThirdPartyBoxWrapper<int>(10).Map(static value => value.ToString());

        Assert.Equal(21, direct.Value);
        Assert.Equal(direct, query);
        Assert.Equal(21, either.Value);
        Assert.Equal("10", wrapped.Unwrap().Value);
    }

    [Fact]
    public void Generic_function_runs_for_two_generated_constructors()
    {
        var latentMonad = new LatentKMonad();
        var latent = KindFunctions.MapTwice(latentMonad, new Latent<int>(5).ToKind(), x => x + 1, x => x * 2);
        var eitherMonad = new EitherKMonad<string>();
        var either = KindFunctions.MapTwice(eitherMonad, new Either<string, int>(5).ToKind(), x => x + 1, x => x * 2);

        Assert.Equal(12, latent.FromKind().Value);
        Assert.Equal(12, either.FromKind().Value);
        Assert.Contains(GeneratedLawRegistry.Instances, registration => registration.WitnessName == "LatentK");
        Assert.All(GeneratedLawRegistry.Instances, registration => Assert.NotNull(registration.Instance));
    }

    [Fact]
    public void Allocation_probe_records_struct_boxing_and_class_carriers()
    {
        var result = KindAllocationProbe.Measure(1_000);

        Assert.Equal(1_000, result.Iterations);
        Assert.True(result.StructCarrierBytes > 0);
        Assert.True(result.ClassCarrierBytes > 0);
    }

    [Fact]
    public void Generator_reports_all_invalid_contracts_at_compile_time()
    {
        const string source = """
            using NetCats.Poc01.GeneratedKinds;
            [GenerateKind("MissingPartialK")]
            public readonly record struct MissingPartial<A>(A Value);
            [GenerateKind("BadArityK")]
            public readonly partial record struct BadArity<A, B, C>(C Value);
            [GenerateKind("BadPositionK", ValueParameter = 4)]
            public readonly partial record struct BadPosition<A>(A Value);
            [GenerateKind("DuplicateK")]
            public readonly partial record struct First<A>(A Value);
            [GenerateKind("DuplicateK")]
            public readonly partial record struct Second<A>(A Value);
            """;
        var diagnostics = RunGenerator(source).Diagnostics.Select(static diagnostic => diagnostic.Id).ToHashSet();

        Assert.Contains("NCGEN001", diagnostics);
        Assert.Contains("NCGEN002", diagnostics);
        Assert.Contains("NCGEN003", diagnostics);
        Assert.Contains("NCGEN004", diagnostics);
    }

    [Fact]
    public void Generated_output_is_deterministic_and_inspectable()
    {
        const string source = """
            using NetCats.Poc01.GeneratedKinds;
            namespace Sample;
            [GenerateKind("BoxK")]
            public readonly partial record struct Box<A>(A Value);
            """;
        var first = RunGenerator(source).GeneratedTrees.Select(static tree => tree.GetText().ToString()).ToArray();
        var second = RunGenerator(source).GeneratedTrees.Select(static tree => tree.GetText().ToString()).ToArray();

        Assert.Equal(first, second);
        Assert.Contains(first, text => text.Contains("sealed class BoxK", StringComparison.Ordinal));
        Assert.Contains(first, text => text.Contains("IMonad<BoxK>", StringComparison.Ordinal));
    }

    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(GenerateKindAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "GeneratorValidation",
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new KindGenerator().AsSourceGenerator());
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }
}
