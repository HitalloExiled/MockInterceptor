using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

#pragma warning disable IDE0046

namespace MockInterceptor.Generator;

[Generator]
public class MockInterceptorGenerator : IIncrementalGenerator
{
    private record struct SyntaxLocation(SyntaxKind Kind, string FilePath, TextSpan TextSpan);
    private record struct RawInterceptedLocation(IMethodSymbol MethodSymbol, string[] Locations);
    private record Invocation(string Type, string LocationAttribute, SyntaxLocation Location);
    private record Registration(string Type, SyntaxLocation Location);

    private static readonly string lineEnding = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\r\n" : "\n";

    private record struct TreeContext(SyntaxTree SyntaxTree, SemanticModel SemanticModel)
    {
        public readonly ISymbol? GetDeclaredSymbol(TextSpan textSpan, CancellationToken cancellationToken = default) =>
            this.SemanticModel.GetDeclaredSymbol(this.GetSyntaxNode(textSpan, cancellationToken), cancellationToken);

        public readonly SymbolInfo GetSymbolInfo(TextSpan textSpan, CancellationToken cancellationToken = default) =>
            this.SemanticModel.GetSymbolInfo(this.GetSyntaxNode(textSpan, cancellationToken), cancellationToken);

        public readonly SyntaxNode? GetSyntaxNode(SyntaxLocation location, CancellationToken cancellationToken = default)
        {
            var root = this.SyntaxTree.GetRoot(cancellationToken);
            var node = root.FindToken(location.TextSpan.Start).Parent;

            while (node != null)
            {
                if (node.IsKind(location.Kind) && node.Span == location.TextSpan)
                {
                    return node;
                }

                node = node.Parent;
            }

            return null;
        }

        public readonly SyntaxNode GetSyntaxNode(TextSpan textSpan, CancellationToken cancellationToken = default) =>
            this.SyntaxTree.GetRoot(cancellationToken).FindNode(textSpan);

        public readonly TypeInfo GetTypeInfo(TextSpan textSpan, CancellationToken cancellationToken = default) =>
            this.SemanticModel.GetTypeInfo(this.GetSyntaxNode(textSpan, cancellationToken), cancellationToken);
    }

    private const string INTERCEPTS_LOCATION_ATTRIBUTE =
    """
    namespace System.Runtime.CompilerServices;

    #pragma warning disable CS9113

    [global::Microsoft.CodeAnalysis.EmbeddedAttribute]
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    public sealed class InterceptsLocationAttribute(global::System.Int32 version, global::System.String data) : global::System.Attribute;
    """;

    private const string MOCK_INTERCEPTOR_ATTRIBUTE_FULLNAME = "MockInterceptor.InterceptAttribute";

    private const string MOCK_INTERCEPTOR_ATTRIBUTE =
    """
    namespace MockInterceptor;

    #pragma warning disable CS9113

    [global::Microsoft.CodeAnalysis.EmbeddedAttribute]
    [global::System.AttributeUsage(global::System.AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class InterceptAttribute(global::System.Type type) : global::System.Attribute;
    """;

    private static readonly SymbolDisplayFormat fullyQualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly SymbolDisplayFormat minimallyQualifiedFormat =
        new(
            SymbolDisplayFormat.MinimallyQualifiedFormat.GlobalNamespaceStyle,
            SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            SymbolDisplayFormat.MinimallyQualifiedFormat.GenericsOptions,
            SymbolDisplayFormat.MinimallyQualifiedFormat.MemberOptions,
            SymbolDisplayFormat.MinimallyQualifiedFormat.DelegateStyle,
            SymbolDisplayFormat.MinimallyQualifiedFormat.ExtensionMethodStyle,
            SymbolDisplayFormat.MinimallyQualifiedFormat.ParameterOptions,
            SymbolDisplayFormat.MinimallyQualifiedFormat.PropertyStyle,
            SymbolDisplayFormat.MinimallyQualifiedFormat.LocalOptions,
            SymbolDisplayFormat.MinimallyQualifiedFormat.KindOptions
        );

    private static string BuildConstraints(IMethodSymbol method)
    {
        if (!method.IsGenericMethod)
        {
            return "";
        }

        var clauses = new List<string>();
        var constraints = new List<string>();

        foreach (var typeParameter in method.TypeParameters)
        {
            if (typeParameter.HasReferenceTypeConstraint)
            {
                constraints.Add("class");
            }
            else if (typeParameter.HasValueTypeConstraint)
            {
                constraints.Add("struct");
            }
            if (typeParameter.HasNotNullConstraint)
            {
                constraints.Add("notnull");
            }
            if (typeParameter.HasUnmanagedTypeConstraint)
            {
                constraints.Add("unmanaged");
            }

            constraints.AddRange(typeParameter.ConstraintTypes.Select(t => t.ToDisplayString(fullyQualifiedFormat)));

            if (typeParameter.HasConstructorConstraint)
            {
                constraints.Add("new()");
            }

            if (constraints.Count > 0)
            {
                clauses.Add($" where {typeParameter.Name} : {string.Join(", ", constraints)}");

                constraints.Clear();
            }
        }

        return clauses.Count == 0 ? "" : string.Concat(clauses);
    }

    private static string BuildParameters(ImmutableArray<IParameterSymbol> parameters, out bool hasUnsafeParameter)
    {
        var builder = new StringBuilder();

        hasUnsafeParameter = false;

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            var modifier = parameter.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In  => "in ",
                _ => parameter.IsParams ? "params " : ""
            };

            var typeName = parameter.Type.ToDisplayString(fullyQualifiedFormat);

            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append($"{modifier}{typeName} {parameter.Name}");

            if (!hasUnsafeParameter && parameter.Type.Kind == SymbolKind.PointerType)
            {
                hasUnsafeParameter = true;
            }
        }

        return builder.ToString();
    }

    private static string GenerateInterceptorSource(INamedTypeSymbol targetType, (IMethodSymbol MethodSymbol, string Location)[] intercerptions)
    {
        var writer = new CodeWriter();

        writer.WriteLine("#if TEST");
        writer.WriteLine("// <auto-generated/>");
        writer.WriteLine("#nullable enable");
        writer.WriteLine("using System.Collections.Concurrent;");
        writer.WriteLine("using System.Runtime.CompilerServices;");
        writer.WriteLine();
        writer.WriteLine("namespace MockInterceptor.Interceptors;");
        writer.WriteLine();

        var targetTypeName      = targetType.ToDisplayString(fullyQualifiedFormat);
        var interfaceName       = $"I{GetSafeTypeName(targetType)}";
        var staticInterfaceName = $"IStatic{GetSafeTypeName(targetType)}";
        var interceptorName     = $"{GetSafeTypeName(targetType)}Interceptor";

        var instanceInterfaceMethods = targetType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(ShouldIncludeInstanceInterface)
            .ToArray();

        var staticInterfaceMethods = targetType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(ShouldIncludeStaticInterface)
            .ToArray();

        if (instanceInterfaceMethods.Length > 0)
        {
            writer.WriteLine($"internal interface {interfaceName}");
            writer.WriteLine("{");
            writer.Indent();

            foreach (var method in instanceInterfaceMethods)
            {
                writer.WriteLine($"{buildInterfaceSignature(method)};");
            }

            writer.Deindent();
            writer.WriteLine("}");
            writer.WriteLine();
        }

        if (staticInterfaceMethods.Length > 0)
        {
            writer.WriteLine($"internal interface {staticInterfaceName}");
            writer.WriteLine("{");
            writer.Indent();

            foreach (var method in staticInterfaceMethods)
            {
                writer.WriteLine($"{buildInterfaceSignature(method)};");
            }

            writer.Deindent();
            writer.WriteLine("}");
            writer.WriteLine();
        }

        writer.WriteLine($"internal static class {interceptorName}");
        writer.WriteLine("{");
        writer.Indent();

        if (instanceInterfaceMethods.Length > 0)
        {
            writer.WriteLine($"private static readonly ConcurrentDictionary<{targetTypeName}, {interfaceName}> mocks = [];");
            writer.WriteLine();
            writer.WriteLine($"public static ConcurrentQueue<{interfaceName}> MockQueue {{ get; }} = [];");
        }

        if (staticInterfaceMethods.Length > 0)
        {
            writer.WriteLine($"public static {staticInterfaceName}? StaticMock {{ get; set; }}");
        }

        writer.WriteLine();

        writer.WriteLine("public static void Reset()");
        writer.WriteLine("{");
        writer.Indent();

        if (instanceInterfaceMethods.Length > 0)
        {
            writer.WriteLine("mocks.Clear();");
            writer.WriteLine("MockQueue.Clear();");
        }

        if (staticInterfaceMethods.Length > 0)
        {
            writer.WriteLine("StaticMock = null;");
        }

        writer.Deindent();
        writer.WriteLine("}");
        writer.WriteLine();

        if (instanceInterfaceMethods.Length > 0)
        {
            writer.WriteLine($"public static bool TryGetOrTrackMock({targetTypeName} instance, [global::System.Diagnostics.CodeAnalysis.NotNullWhenAttribute(true)] out {interfaceName}? mock)");
            writer.WriteLine("{");
                writer.Indent();
                writer.WriteLine("if (mocks.TryGetValue(instance, out mock))");
                writer.WriteLine("{");
                    writer.Indent();
                    writer.WriteLine("return true;");
                    writer.Deindent();
                writer.WriteLine("}");
                writer.WriteLine();
                writer.WriteLine("if (MockQueue.TryDequeue(out mock))");
                writer.WriteLine("{");
                    writer.Indent();
                    writer.WriteLine("mocks[instance] = mock;");
                    writer.WriteLine("return true;");
                    writer.Deindent();
                writer.WriteLine("}");
                writer.WriteLine();
                writer.WriteLine("mock = null;");
                writer.WriteLine("return false;");
                writer.Deindent();
            writer.WriteLine("}");
            writer.WriteLine();
        }

        var groupedMethods = intercerptions
            .GroupBy(static x => x.MethodSymbol, static x => x.Location, SymbolEqualityComparer.Default)
            .ToArray();

        for (var i = 0; i < groupedMethods.Length; i++)
        {
            var group = groupedMethods[i];

            if (i > 0)
            {
                writer.WriteLine();
            }

            WriteMethodInterceptor(writer, targetTypeName, (IMethodSymbol)group.Key!, [.. group]);
        }

        writer.Deindent();
        writer.WriteLine("}");
        writer.WriteLine("#endif");

        return writer.ToString();

        static string buildInterfaceSignature(IMethodSymbol method)
        {
            var returnType     = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(fullyQualifiedFormat);
            var typeParameters = method.IsGenericMethod
                ? $"<{string.Join(", ", method.TypeParameters.Select(x => x.Name))}>"
                : "";

            var parameters  = BuildParameters(method.Parameters, out var hasUnsafeParameter);
            var constraints = BuildConstraints(method);

            var unsafeModifier = hasUnsafeParameter || method.ReturnType.Kind == SymbolKind.PointerType
                ? "unsafe "
                : "" ;

            return $"{unsafeModifier}{returnType} {method.Name}{typeParameters}({parameters}){constraints}";
        }
    }

    private static void GenerateOutput(SourceProductionContext context, Compilation compilation, ImmutableArray<Registration> registrations, ImmutableArray<Invocation> invocations)
    {
        if (registrations.IsDefaultOrEmpty || invocations.IsDefaultOrEmpty)
        {
            return;
        }

        var cancellationToken = context.CancellationToken;

        var treeContextMap = compilation.SyntaxTrees.ToDictionary(static x => x.FilePath, x => new TreeContext(x, compilation.GetSemanticModel(x)));

        foreach (var registration in GetUniqueRegistrations(registrations))
        {
            if (!treeContextMap.TryGetValue(registration.Location.FilePath, out var registrationTreeContext))
            {
                continue;
            }

            var typeOfExpression = (TypeOfExpressionSyntax)registrationTreeContext.GetSyntaxNode(registration.Location, cancellationToken)!;

            var target = (INamedTypeSymbol)registrationTreeContext.SemanticModel.GetTypeInfo(typeOfExpression.Type, cancellationToken).Type!;

            var builder = ImmutableArray.CreateBuilder<(IMethodSymbol, string)>();

            foreach (var invocation in invocations)
            {
                if (invocation.Type == registration.Type && treeContextMap.TryGetValue(invocation.Location.FilePath, out var callSiteTreeContext))
                {
                    var invocationExpression = (InvocationExpressionSyntax)callSiteTreeContext.GetSyntaxNode(invocation.Location, cancellationToken)!;

                    var methodSymbol = ((IMethodSymbol)callSiteTreeContext.SemanticModel.GetSymbolInfo(invocationExpression.Expression, cancellationToken).Symbol!).ConstructedFrom;

                    builder.Add((methodSymbol, invocation.LocationAttribute));
                }
            }

            var interceptions = builder.ToArray();

            if (interceptions.Length == 0)
            {
                continue;
            }

            var source = GenerateInterceptorSource(target, interceptions);

            if (string.IsNullOrEmpty(source))
            {
                return;
            }

            context.AddSource($"{GetSafeTypeName(target)}Interceptor.g.cs", SourceText.From(source, Encoding.UTF8));
        }
    }

    private static Invocation? GetInvocations(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var invocationExpression = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocationExpression, cancellationToken).Symbol is not IMethodSymbol methodSymbol)
        {
            return null;
        }

        if (methodSymbol.ConstructedFrom.MethodKind != MethodKind.Ordinary)
        {
            return null;
        }

        if (
            methodSymbol.MethodKind != MethodKind.Ordinary
            || methodSymbol.IsImplicitlyDeclared
            || methodSymbol.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal
        )
        {
            return null;
        }

        if (methodSymbol.ConstructedFrom.ContainingType is not INamedTypeSymbol containerTypeSymbol)
        {
            return null;
        }

        if (context.SemanticModel.GetInterceptableLocation(invocationExpression, cancellationToken) is not InterceptableLocation interceptableLocation)
        {
            return null;
        }

        var location = new SyntaxLocation(
            invocationExpression.Kind(),
            invocationExpression.SyntaxTree.FilePath,
            invocationExpression.Span
        );

        return new(
            containerTypeSymbol.ToDisplayString(fullyQualifiedFormat),
            interceptableLocation.GetInterceptsLocationAttributeSyntax(),
            location
        );
    }

    private static Registration? GetRegistration(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var attributeSyntax = (AttributeSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(attributeSyntax, cancellationToken).Symbol is not IMethodSymbol { ContainingType: var attributeType })
        {
            return null;
        }

        if (attributeType.ToDisplayString() != MOCK_INTERCEPTOR_ATTRIBUTE_FULLNAME)
        {
            return null;
        }

        if (attributeSyntax.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not TypeOfExpressionSyntax typeOfExpressionSyntax)
        {
            return null;
        }

        if (context.SemanticModel.GetTypeInfo(typeOfExpressionSyntax.Type, cancellationToken).Type is not INamedTypeSymbol type)
        {
            return null;
        }

        if (type.DeclaringSyntaxReferences.Length == 0)
        {
            return null;
        }

        return new(
            type.ToDisplayString(fullyQualifiedFormat),
            new(
                typeOfExpressionSyntax.Kind(),
                typeOfExpressionSyntax.SyntaxTree.FilePath,
                typeOfExpressionSyntax.Span
            )
        );
    }

    private static ImmutableArray<Registration> GetUniqueRegistrations(ImmutableArray<Registration> registrations)
    {
        if (registrations.IsDefaultOrEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<Registration>();

        foreach (var registration in registrations)
        {
            var exists = false;

            foreach (var existing in builder)
            {
                if (existing == registration)
                {
                    exists = true;

                    break;
                }
            }

            if (!exists)
            {
                builder.Add(registration);
            }
        }

        return builder.ToImmutable();
    }

    private static string GetSafeTypeName(INamedTypeSymbol symbol)
    {
        var builder = new StringBuilder();

        foreach (var part in symbol.ToDisplayParts(minimallyQualifiedFormat))
        {
            if (part.ToString() is "." or "<" or ">")
            {
                continue;
            }

            builder.Append(part);
        }

        return builder.ToString();
    }

    private static bool ShouldIncludeInstanceInterface(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Ordinary &&
        method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal &&
        !method.IsStatic &&
        !method.IsImplicitlyDeclared;

    private static bool ShouldIncludeStaticInterface(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Ordinary &&
        method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal &&
        method.IsStatic &&
        !method.IsImplicitlyDeclared;

    private static void PostInitializationCallback(IncrementalGeneratorPostInitializationContext context)
    {
        context.AddEmbeddedAttributeDefinition();
        context.AddSource("InterceptsLocationAttribute.g.cs", SourceText.From(INTERCEPTS_LOCATION_ATTRIBUTE.ReplaceLineEndings(lineEnding), Encoding.UTF8));
        context.AddSource("InterceptAttribute.g.cs", SourceText.From(MOCK_INTERCEPTOR_ATTRIBUTE.ReplaceLineEndings(lineEnding), Encoding.UTF8));
    }

    private static void WriteMethodInterceptor(CodeWriter writer, string targetName, IMethodSymbol method, string[] locations)
    {
        var returnType     = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(fullyQualifiedFormat);
        var methodName     = method.Name;
        var typeParameters = method.IsGenericMethod ? $"<{string.Join(", ", method.TypeParameters.Select(x => x.Name))}>" : "";
        var arguments      = string.Join(", ", method.Parameters.Select(buildArguments));
        var constraints    = BuildConstraints(method);
        var parameters     = BuildParameters(method.Parameters, out var hasUnsafeParameter);
        var unsafeModifier = hasUnsafeParameter || method.ReturnType.Kind == SymbolKind.PointerType
            ? " unsafe"
            : "";

        foreach (var location in locations)
        {
            writer.WriteLine(location);
        }

        if (method.IsStatic)
        {
            writer.WriteLine($"public{unsafeModifier} static {returnType} {methodName}{typeParameters}({parameters}){constraints} =>");
                writer.Indent();
                writer.WriteLine("StaticMock != null");
                    writer.Indent();
                    writer.WriteLine($"? StaticMock.{methodName}{typeParameters}({arguments})");
                    writer.WriteLine($": {targetName}.{methodName}{typeParameters}({arguments});");
                    writer.Deindent();
                writer.Deindent();
        }
        else
        {
            var separator = !string.IsNullOrEmpty(parameters)
                ? ", "
                : "";

            writer.WriteLine($"public{unsafeModifier} static {returnType} {methodName}{typeParameters}(this {targetName} instance{separator}{parameters}){constraints} =>");
                writer.Indent();
                writer.WriteLine("TryGetOrTrackMock(instance, out var mock)");
                    writer.Indent();
                    writer.WriteLine($"? mock.{methodName}{typeParameters}({arguments})");
                    writer.WriteLine($": instance.{methodName}{typeParameters}({arguments});");
                    writer.Deindent();
                writer.Deindent();
        }

        static string buildArguments(IParameterSymbol parameter)
        {
            var modifier = parameter.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                _ => ""
            };

            return modifier + parameter.Name;
        }
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(PostInitializationCallback);

        var registrations = context.SyntaxProvider.CreateSyntaxProvider(static (node, _) => node is AttributeSyntax, GetRegistration);
        var invocations   = context.SyntaxProvider.CreateSyntaxProvider(static (node, _) => node is InvocationExpressionSyntax, GetInvocations);

        var aggregated = context.CompilationProvider
            .Combine(registrations.WhereNotNull().Collect())
            .Combine(invocations.WhereNotNull().Collect());

        context.RegisterSourceOutput(aggregated, static (context, data) => GenerateOutput(context, data.Left.Left, data.Left.Right, data.Right));
    }
}
