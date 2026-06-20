using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace BareWire.Analyzers.Tests.Verifiers;

/// <summary>
/// Thin wrapper around <see cref="CSharpAnalyzerVerifier{TAnalyzer, TVerifier}"/> that pre-configures
/// the test to use .NET 8 reference assemblies (sufficient for Task/CancellationToken BCL types).
/// </summary>
internal static class CSharpAnalyzerVerifier<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    /// <summary>
    /// Verifies that <paramref name="source"/> produces exactly <paramref name="expected"/> diagnostics.
    /// Inline markup (<c>{|BW1001:...|}</c>) in <paramref name="source"/> is also supported.
    /// </summary>
    internal static Task VerifyAnalyzerAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new Test { TestCode = source };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    /// <summary>
    /// Creates a <see cref="DiagnosticResult"/> for the <typeparamref name="TAnalyzer"/>'s first
    /// supported descriptor — convenience shortcut for inline location markup.
    /// </summary>
    public static DiagnosticResult Diagnostic()
        => CSharpAnalyzerVerifier<TAnalyzer, DefaultVerifier>.Diagnostic();

    /// <summary>
    /// Creates a <see cref="DiagnosticResult"/> for the given diagnostic ID.
    /// </summary>
    public static DiagnosticResult Diagnostic(string diagnosticId)
        => CSharpAnalyzerVerifier<TAnalyzer, DefaultVerifier>.Diagnostic(diagnosticId);

    /// <summary>
    /// xunit-aware test class that sets reference assemblies to .NET 8.
    /// </summary>
    private sealed class Test : CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
    {
        public Test()
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
        }
    }
}
