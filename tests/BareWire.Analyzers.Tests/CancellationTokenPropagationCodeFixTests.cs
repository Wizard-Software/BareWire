using BareWire.Analyzers.Tests.Verifiers;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace BareWire.Analyzers.Tests;

/// <summary>
/// Tests for <see cref="CancellationTokenPropagationCodeFixProvider"/> covering plan §4 code-fix cases (a)-(c).
/// </summary>
public sealed class CancellationTokenPropagationCodeFixTests
{
    // ── (a) Method with existing params gets CancellationToken appended ────────────────────────────

    [Fact]
    public async Task VerifyCodeFix_MethodWithParams_AppendsCancellationToken()
    {
        // The markup {|BW1001:MethodName|} matches method.Locations[0] (the method name identifier)
        var source = """
            using System.Threading.Tasks;
            public class Publisher
            {
                public Task {|BW1001:PublishAsync|}(string msg)
                    => Task.CompletedTask;
            }
            """;

        var fixedSource = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Publisher
            {
                public Task PublishAsync(string msg, CancellationToken cancellationToken = default)
                    => Task.CompletedTask;
            }
            """;

        await CSharpCodeFixVerifier<CancellationTokenPropagationAnalyzer, CancellationTokenPropagationCodeFixProvider>
            .VerifyCodeFixAsync(source, DiagnosticResult.EmptyDiagnosticResults, fixedSource);
    }

    // ── (b) Parameterless method gets CancellationToken as first/only parameter ──────────────────

    [Fact]
    public async Task VerifyCodeFix_ParameterlessMethod_AddsCancellationTokenAsFirstParam()
    {
        var source = """
            using System.Threading.Tasks;
            public class Bootstrapper
            {
                public Task {|BW1001:InitializeAsync|}()
                    => Task.CompletedTask;
            }
            """;

        var fixedSource = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Bootstrapper
            {
                public Task InitializeAsync(CancellationToken cancellationToken = default)
                    => Task.CompletedTask;
            }
            """;

        await CSharpCodeFixVerifier<CancellationTokenPropagationAnalyzer, CancellationTokenPropagationCodeFixProvider>
            .VerifyCodeFixAsync(source, DiagnosticResult.EmptyDiagnosticResults, fixedSource);
    }

    // ── (c) using System.Threading is added when missing ─────────────────────────────────────────

    [Fact]
    public async Task VerifyCodeFix_MissingUsingSystemThreading_AddsUsingDirective()
    {
        // Source has NO using System.Threading — the code fix must add it
        var source = """
            using System.Threading.Tasks;
            public class Worker
            {
                public Task {|BW1001:DoWorkAsync|}(int count)
                    => Task.CompletedTask;
            }
            """;

        var fixedSource = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Worker
            {
                public Task DoWorkAsync(int count, CancellationToken cancellationToken = default)
                    => Task.CompletedTask;
            }
            """;

        await CSharpCodeFixVerifier<CancellationTokenPropagationAnalyzer, CancellationTokenPropagationCodeFixProvider>
            .VerifyCodeFixAsync(source, DiagnosticResult.EmptyDiagnosticResults, fixedSource);
    }
}
