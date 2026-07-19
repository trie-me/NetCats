using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace NetCats.Poc01.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class KindGenerator : IIncrementalGenerator
{
    private const string AttributeName = "NetCats.Poc01.GeneratedKinds.GenerateKindAttribute";

    private static readonly DiagnosticDescriptor UnsupportedArity = new(
        "NCGEN001",
        "Unsupported type-constructor arity",
        "Type '{0}' has arity {1}; the POC generator supports unary and binary constructors",
        "NetCats.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidValueParameter = new(
        "NCGEN002",
        "Invalid value parameter position",
        "ValueParameter {0} is outside the generic parameter list for '{1}'",
        "NetCats.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateWitness = new(
        "NCGEN003",
        "Duplicate kind witness",
        "Witness name '{0}' is registered more than once",
        "NetCats.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingPartial = new(
        "NCGEN004",
        "Kind carrier must be partial",
        "Type '{0}' must be declared partial so generated and handwritten surfaces can evolve together",
        "NetCats.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var specifications = context.SyntaxProvider.ForAttributeWithMetadataName(
                AttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, _) => CreateSpecification(attributeContext))
            .Where(static specification => specification is not null)
            .Select(static (specification, _) => specification!);

        context.RegisterSourceOutput(specifications, static (productionContext, specification) =>
        {
            if (!Validate(productionContext, specification))
            {
                return;
            }

            productionContext.AddSource(
                specification.HintName + ".g.cs",
                SourceText.From(Render(specification), Encoding.UTF8));
        });

        context.RegisterSourceOutput(specifications.Collect(), static (productionContext, items) =>
        {
            ReportDuplicates(productionContext, items);
            var valid = items.Where(static item => item.Arity is 1 or 2 &&
                                                   item.ValueParameter >= 0 &&
                                                   item.ValueParameter < item.Arity &&
                                                   item.IsPartial)
                .ToArray();
            productionContext.AddSource(
                "GeneratedLawRegistry.g.cs",
                SourceText.From(RenderRegistry(valid), Encoding.UTF8));
        });
    }

    private static KindSpecification? CreateSpecification(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        var attribute = context.Attributes[0];
        var witnessName = attribute.ConstructorArguments.Length == 1 &&
                          attribute.ConstructorArguments[0].Value is string configuredName
            ? configuredName
            : type.Name + "K";

        var valueParameter = 0;
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == "ValueParameter" && argument.Value.Value is int configuredPosition)
            {
                valueParameter = configuredPosition;
            }
        }

        var declaration = (TypeDeclarationSyntax)context.TargetNode;
        var isPartial = declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword));
        var location = declaration.AttributeLists
            .SelectMany(static list => list.Attributes)
            .FirstOrDefault()?.GetLocation() ?? declaration.Identifier.GetLocation();

        return new KindSpecification(
            type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString(),
            type.Name,
            witnessName,
            type.TypeParameters.Select(static parameter => parameter.Name).ToImmutableArray(),
            valueParameter,
            isPartial,
            location);
    }

    private static bool Validate(SourceProductionContext context, KindSpecification specification)
    {
        var valid = true;
        if (specification.Arity is < 1 or > 2)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnsupportedArity,
                specification.Location,
                specification.TypeName,
                specification.Arity));
            valid = false;
        }

        if (specification.ValueParameter < 0 || specification.ValueParameter >= specification.Arity)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidValueParameter,
                specification.Location,
                specification.ValueParameter,
                specification.TypeName));
            valid = false;
        }

        if (!specification.IsPartial)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingPartial, specification.Location, specification.TypeName));
            valid = false;
        }

        return valid;
    }

    private static void ReportDuplicates(SourceProductionContext context, ImmutableArray<KindSpecification> items)
    {
        foreach (var group in items.GroupBy(static item => item.WitnessName, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            foreach (var item in group)
            {
                context.ReportDiagnostic(Diagnostic.Create(DuplicateWitness, item.Location, item.WitnessName));
            }
        }
    }

    private static string Render(KindSpecification specification)
    {
        var sourceTypeParameters = string.Join(", ", specification.TypeParameters);
        var valueName = specification.TypeParameters[specification.ValueParameter];
        var fixedParameters = specification.TypeParameters
            .Where((_, index) => index != specification.ValueParameter)
            .ToArray();
        var fixedList = string.Join(", ", fixedParameters);
        var witnessUse = fixedParameters.Length == 0
            ? specification.WitnessName
            : specification.WitnessName + "<" + fixedList + ">";
        var witnessDeclaration = fixedParameters.Length == 0
            ? specification.WitnessName
            : specification.WitnessName + "<" + fixedList + ">";
        var instanceName = specification.WitnessName + "Monad";
        var instanceDeclaration = fixedParameters.Length == 0
            ? instanceName
            : instanceName + "<" + fixedList + ">";
        var methodFixedPrefix = fixedParameters.Length == 0 ? string.Empty : fixedList + ", ";
        var carrierA = ConstructCarrier(specification, "A");
        var carrierB = ConstructCarrier(specification, "B");
        var carrierC = ConstructCarrier(specification, "C");
        var namespaceOpen = string.IsNullOrEmpty(specification.Namespace)
            ? string.Empty
            : "namespace " + specification.Namespace + "\n{\n";
        var namespaceClose = string.IsNullOrEmpty(specification.Namespace) ? string.Empty : "}\n";

        return "// <auto-generated/>\n" +
               "#nullable enable\n" +
               namespaceOpen +
               "public sealed class " + witnessDeclaration + " { private " + specification.WitnessName + "() { } }\n\n" +
               "public sealed class " + instanceDeclaration + " : global::NetCats.Poc01.GeneratedKinds.IMonad<" + witnessUse + ">\n" +
               "{\n" +
               "    public global::NetCats.Poc01.GeneratedKinds.K<" + witnessUse + ", B> Pure<B>(B value) => new(" + NewCarrier(specification, "B", "value") + ");\n" +
               "    public global::NetCats.Poc01.GeneratedKinds.K<" + witnessUse + ", B> Map<A, B>(global::NetCats.Poc01.GeneratedKinds.K<" + witnessUse + ", A> source, global::System.Func<A, B> selector)\n" +
               "        => new(" + NewCarrier(specification, "B", "selector(source.Get<" + carrierA + ">().Value)") + ");\n" +
               "    public global::NetCats.Poc01.GeneratedKinds.K<" + witnessUse + ", B> Bind<A, B>(global::NetCats.Poc01.GeneratedKinds.K<" + witnessUse + ", A> source, global::System.Func<A, global::NetCats.Poc01.GeneratedKinds.K<" + witnessUse + ", B>> selector)\n" +
               "        => selector(source.Get<" + carrierA + ">().Value);\n" +
               "}\n\n" +
               "public static class " + specification.WitnessName + "Extensions\n" +
               "{\n" +
               "    public static global::NetCats.Poc01.GeneratedKinds.K<" + witnessUse + ", A> ToKind<" + methodFixedPrefix + "A>(this " + carrierA + " source) => new(source);\n" +
               "    public static " + carrierA + " FromKind<" + methodFixedPrefix + "A>(this global::NetCats.Poc01.GeneratedKinds.K<" + witnessUse + ", A> source) => source.Get<" + carrierA + ">();\n" +
               "    public static " + carrierB + " Select<" + methodFixedPrefix + "A, B>(this " + carrierA + " source, global::System.Func<A, B> selector) => " + NewCarrier(specification, "B", "selector(source.Value)") + ";\n" +
               "    public static " + carrierB + " Map<" + methodFixedPrefix + "A, B>(this " + carrierA + " source, global::System.Func<A, B> selector) => source.Select(selector);\n" +
               "    public static " + carrierB + " SelectMany<" + methodFixedPrefix + "A, B>(this " + carrierA + " source, global::System.Func<A, " + carrierB + "> selector) => selector(source.Value);\n" +
               "    public static " + carrierB + " Bind<" + methodFixedPrefix + "A, B>(this " + carrierA + " source, global::System.Func<A, " + carrierB + "> selector) => source.SelectMany(selector);\n" +
               "    public static " + carrierC + " SelectMany<" + methodFixedPrefix + "A, B, C>(this " + carrierA + " source, global::System.Func<A, " + carrierB + "> selector, global::System.Func<A, B, C> projector)\n" +
               "    {\n" +
               "        var value = source.Value;\n" +
               "        return " + NewCarrier(specification, "C", "projector(value, selector(value).Value)") + ";\n" +
               "    }\n" +
               "}\n" +
               namespaceClose;
    }

    private static string RenderRegistry(IReadOnlyCollection<KindSpecification> specifications)
    {
        var registrations = specifications
            .Where(static specification => specification.TypeParameters.Length == 1)
            .Select(static specification =>
            {
                var qualified = string.IsNullOrEmpty(specification.Namespace)
                    ? "global::" + specification.WitnessName + "Monad"
                    : "global::" + specification.Namespace + "." + specification.WitnessName + "Monad";
                return "        new global::NetCats.Poc01.GeneratedKinds.GeneratedLawRegistration(\"" +
                       specification.WitnessName + "\", new " + qualified + "())";
            });

        return "// <auto-generated/>\n" +
               "#nullable enable\n" +
               "namespace NetCats.Poc01.GeneratedKinds\n" +
               "{\n" +
               "    public static class GeneratedLawRegistry\n" +
               "    {\n" +
               "        public static global::System.Collections.Generic.IReadOnlyList<GeneratedLawRegistration> Instances { get; } = new GeneratedLawRegistration[]\n" +
               "        {\n" +
               string.Join(",\n", registrations) + "\n" +
               "        };\n" +
               "    }\n" +
               "}\n";
    }

    private static string ConstructCarrier(KindSpecification specification, string valueType)
    {
        var arguments = specification.TypeParameters
            .Select((parameter, index) => index == specification.ValueParameter ? valueType : parameter);
        var prefix = string.IsNullOrEmpty(specification.Namespace)
            ? "global::"
            : "global::" + specification.Namespace + ".";
        return prefix + specification.TypeName + "<" + string.Join(", ", arguments) + ">";
    }

    private static string NewCarrier(KindSpecification specification, string valueType, string expression)
    {
        return "new " + ConstructCarrier(specification, valueType) + "(" + expression + ")";
    }

    private sealed class KindSpecification
    {
        public KindSpecification(
            string @namespace,
            string typeName,
            string witnessName,
            ImmutableArray<string> typeParameters,
            int valueParameter,
            bool isPartial,
            Location location)
        {
            Namespace = @namespace;
            TypeName = typeName;
            WitnessName = witnessName;
            TypeParameters = typeParameters;
            ValueParameter = valueParameter;
            IsPartial = isPartial;
            Location = location;
        }

        public string Namespace { get; }
        public string TypeName { get; }
        public string WitnessName { get; }
        public ImmutableArray<string> TypeParameters { get; }
        public int ValueParameter { get; }
        public bool IsPartial { get; }
        public Location Location { get; }
        public int Arity => TypeParameters.Length;
        public string HintName => TypeName + "." + WitnessName;
    }
}
