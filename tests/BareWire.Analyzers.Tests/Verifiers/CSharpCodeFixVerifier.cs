using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace BareWire.Analyzers.Tests.Verifiers;

/// <summary>
/// Thin wrapper around <see cref="CSharpCodeFixVerifier{TAnalyzer, TCodeFix, TVerifier}"/> that
/// pre-configures the test to use .NET 8 reference assemblies.
/// </summary>
internal static class CSharpCodeFixVerifier<TAnalyzer, TCodeFix>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TCodeFix : CodeFixProvider, new()
{
    /// <summary>
    /// Creates a <see cref="DiagnosticResult"/> for the <typeparamref name="TAnalyzer"/>'s first
    /// supported descriptor — convenience shortcut for inline location markup.
    /// </summary>
    public static DiagnosticResult Diagnostic()
        => CSharpCodeFixVerifier<TAnalyzer, TCodeFix, DefaultVerifier>.Diagnostic();

    /// <summary>
    /// Verifies that <paramref name="source"/> produces exactly <paramref name="expected"/> diagnostics
    /// and no code fix is needed.
    /// </summary>
    internal static Task VerifyAnalyzerAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new Test { TestCode = source };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    /// <summary>
    /// Verifies that applying the code fix to <paramref name="source"/> (with <paramref name="expected"/>
    /// diagnostics) produces <paramref name="fixedSource"/>.
    /// </summary>
    internal static Task VerifyCodeFixAsync(
        string source,
        DiagnosticResult[] expected,
        string fixedSource)
    {
        var test = new Test
        {
            TestCode = source,
            FixedCode = fixedSource,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    /// <summary>
    /// Convenience overload for a single expected diagnostic.
    /// </summary>
    internal static Task VerifyCodeFixAsync(
        string source,
        DiagnosticResult expected,
        string fixedSource) =>
        VerifyCodeFixAsync(source, [expected], fixedSource);

    /// <summary>
    /// xunit-aware test class that sets reference assemblies to .NET 8.
    /// </summary>
    private sealed class Test : CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
    {
        public Test()
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
        }
    }
}
