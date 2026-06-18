using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace BareWire.Analyzers;

/// <summary>
/// Analyzer that enforces rule BW1001: every public method returning <c>Task</c>, <c>ValueTask</c>,
/// <c>Task&lt;T&gt;</c>, or <c>ValueTask&lt;T&gt;</c> must accept <see cref="System.Threading.CancellationToken"/>
/// as its last parameter.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CancellationTokenPropagationAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray =
        ImmutableArray.Create(DiagnosticDescriptors.CancellationTokenPropagation);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SupportedDiagnosticsArray;

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        // Do not analyze generated code — avoids noise on code the developer does not control.
        // Verifier §9 decision: GeneratedCodeAnalysisFlags.None.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            // Build per-compilation KnownTypes. NEVER store as static/field — that would root
            // the Compilation and cause stale symbols across compilations (perf impl-guard §13/1).
            var known = KnownTypes.From(compilationStart.Compilation);

            // Guard: if any BCL type cannot be resolved (e.g. missing reference), skip entirely.
            if (!known.IsComplete)
                return;

            compilationStart.RegisterSymbolAction(
                ctx => Analyze(ctx, known),
                SymbolKind.Method);
        });
    }

    private static void Analyze(SymbolAnalysisContext context, KnownTypes known)
    {
        var method = (IMethodSymbol)context.Symbol;

        // Filter 1: only ordinary methods (skip constructors, accessors, operators, local functions)
        if (method.MethodKind != MethodKind.Ordinary)
            return;

        // Filter 2: only public methods
        if (method.DeclaredAccessibility != Accessibility.Public)
            return;

        // Filter 3: return type must be Task / ValueTask / Task<T> / ValueTask<T>
        if (!known.IsAsyncReturnType(method.ReturnType))
            return;

        // Filter 4: skip overrides and interface implementations — the author does not control
        // the base signature (verifier §9 decision).
        if (method.IsOverride)
            return;

        if (!method.ExplicitInterfaceImplementations.IsEmpty)
            return;

        // Check whether the method also implicitly implements an interface member
        // (e.g. public Task DoAsync() in a class that implements IFoo.DoAsync()).
        // We use the containing type's interfaces to detect this without calling a slow helper.
        if (IsImplicitInterfaceImplementation(method))
            return;

        // Diagnostic condition: last parameter is not CancellationToken (or no parameters at all)
        var parameters = method.Parameters;
        if (parameters.Length > 0 &&
            SymbolEqualityComparer.Default.Equals(parameters[parameters.Length - 1].Type, known.CancellationToken))
        {
            // Last parameter is CancellationToken — compliant
            return;
        }

        // Guard: a symbol with no source location (synthesized / metadata-only edge case) would
        // throw on Locations[0]; degrade gracefully instead of crashing the analyzer (SEC-1).
        var location = method.Locations.Length > 0 ? method.Locations[0] : Location.None;

        context.ReportDiagnostic(
            Diagnostic.Create(
                DiagnosticDescriptors.CancellationTokenPropagation,
                location,
                method.Name));
    }

    private static bool IsImplicitInterfaceImplementation(IMethodSymbol method)
    {
        var containingType = method.ContainingType;
        if (containingType is null || containingType.AllInterfaces.Length == 0)
            return false;

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(method.Name))
            {
                // Cheap name gate (GetMembers(name)) eliminates the vast majority of the
                // expensive FindImplementationForInterfaceMember calls (PERF-1).
                if (member is IMethodSymbol interfaceMethod)
                {
                    var impl = containingType.FindImplementationForInterfaceMember(interfaceMethod);
                    if (SymbolEqualityComparer.Default.Equals(impl, method))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Holds the well-known BCL type symbols resolved once per compilation.
    /// Immutable value type — safe for concurrent access.
    /// </summary>
    private readonly struct KnownTypes
    {
        public readonly INamedTypeSymbol? Task;
        public readonly INamedTypeSymbol? TaskOfT;
        public readonly INamedTypeSymbol? ValueTask;
        public readonly INamedTypeSymbol? ValueTaskOfT;
        public readonly INamedTypeSymbol? CancellationToken;

        private KnownTypes(
            INamedTypeSymbol? task,
            INamedTypeSymbol? taskOfT,
            INamedTypeSymbol? valueTask,
            INamedTypeSymbol? valueTaskOfT,
            INamedTypeSymbol? cancellationToken)
        {
            Task = task;
            TaskOfT = taskOfT;
            ValueTask = valueTask;
            ValueTaskOfT = valueTaskOfT;
            CancellationToken = cancellationToken;
        }

        /// <summary>Returns <see langword="true"/> when all BCL types were resolved successfully.</summary>
        public bool IsComplete =>
            Task is not null &&
            TaskOfT is not null &&
            ValueTask is not null &&
            ValueTaskOfT is not null &&
            CancellationToken is not null;

        public static KnownTypes From(Compilation compilation) => new KnownTypes(
            task: compilation.GetTypeByMetadataName("System.Threading.Tasks.Task"),
            taskOfT: compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1"),
            valueTask: compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask"),
            valueTaskOfT: compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1"),
            cancellationToken: compilation.GetTypeByMetadataName("System.Threading.CancellationToken"));

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="type"/> is one of the recognized
        /// async return types (Task, ValueTask, Task&lt;T&gt;, ValueTask&lt;T&gt;).
        /// Uses <c>OriginalDefinition</c> for generic variants to match across type arguments.
        /// </summary>
        public bool IsAsyncReturnType(ITypeSymbol type)
        {
            if (SymbolEqualityComparer.Default.Equals(type, Task))
                return true;
            if (SymbolEqualityComparer.Default.Equals(type, ValueTask))
                return true;

            // For generic variants, compare the unbound generic definition
            var originalDef = (type as INamedTypeSymbol)?.OriginalDefinition;
            if (originalDef is null)
                return false;

            return SymbolEqualityComparer.Default.Equals(originalDef, TaskOfT) ||
                   SymbolEqualityComparer.Default.Equals(originalDef, ValueTaskOfT);
        }
    }
}
