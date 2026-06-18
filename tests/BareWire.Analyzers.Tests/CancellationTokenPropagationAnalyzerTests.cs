using BareWire.Analyzers.Tests.Verifiers;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace BareWire.Analyzers.Tests;

/// <summary>
/// Tests for <see cref="CancellationTokenPropagationAnalyzer"/> covering all plan §4 cases (a)-(h).
/// </summary>
public sealed class CancellationTokenPropagationAnalyzerTests
{
    // ── (a) Public Task method without token → BW1001 ────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_PublicTaskMethodWithoutToken_ReportsBW1001()
    {
        // Markup {|BW1001:MethodName|} targets the method identifier — that is method.Locations[0]
        var source = """
            using System.Threading.Tasks;
            public class Publisher
            {
                public Task {|BW1001:PublishAsync|}(string message)
                    => Task.CompletedTask;
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    // ── (b) Public Task method with token as last param → no diagnostic ────────────────────────

    [Fact]
    public async Task PublishAsync_PublicTaskMethodWithTokenLast_NoDiagnostic()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Publisher
            {
                public Task PublishAsync(string message, CancellationToken cancellationToken = default)
                    => Task.CompletedTask;
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    // ── (c) Generic variants: ValueTask<int> and Task<T> ─────────────────────────────────────────

    [Fact]
    public async Task GetAsync_PublicValueTaskOfIntWithoutToken_ReportsBW1001()
    {
        var source = """
            using System.Threading.Tasks;
            public class Reader
            {
                public ValueTask<int> {|BW1001:GetAsync|}(string key)
                    => ValueTask.FromResult(0);
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task FetchAsync_PublicTaskOfStringWithoutToken_ReportsBW1001()
    {
        var source = """
            using System.Threading.Tasks;
            public class Fetcher
            {
                public Task<string> {|BW1001:FetchAsync|}(int id)
                    => Task.FromResult("result");
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    // ── (d) Internal/private async without token → no diagnostic ─────────────────────────────────

    [Fact]
    public async Task InternalMethod_AsyncWithoutToken_NoDiagnostic()
    {
        var source = """
            using System.Threading.Tasks;
            public class Worker
            {
                internal Task DoInternalWorkAsync() => Task.CompletedTask;
                private Task DoPrivateWorkAsync() => Task.CompletedTask;
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    // ── (e) Sync void/int without token → no diagnostic ──────────────────────────────────────────

    [Fact]
    public async Task SyncMethod_VoidAndInt_NoDiagnostic()
    {
        var source = """
            public class Calculator
            {
                public void Reset() { }
                public int Add(int a, int b) => a + b;
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    // ── (f) Public parameterless async → BW1001 ──────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_PublicParameterlessTask_ReportsBW1001()
    {
        var source = """
            using System.Threading.Tasks;
            public class Bootstrapper
            {
                public Task {|BW1001:InitializeAsync|}()
                    => Task.CompletedTask;
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    // ── (g) CancellationToken not in last position → BW1001 ──────────────────────────────────────

    [Fact]
    public async Task SendAsync_TokenNotLast_ReportsBW1001()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Sender
            {
                public Task {|BW1001:SendAsync|}(CancellationToken cancellationToken, string payload)
                    => Task.CompletedTask;
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    // ── (h) Override / interface implementation → no diagnostic ──────────────────────────────────

    [Fact]
    public async Task OverrideMethod_AsyncWithoutToken_NoDiagnostic()
    {
        // The abstract base method (without CancellationToken) triggers BW1001 because
        // the base author controls that signature. The override in Derived does NOT trigger
        // because the Derived author cannot change the signature from the base.
        // Test: ensure only the abstract base triggers, not the override.
        var source = """
            using System.Threading.Tasks;
            public abstract class Base
            {
                public abstract Task {|BW1001:ExecuteAsync|}();
            }
            public class Derived : Base
            {
                public override Task ExecuteAsync() => Task.CompletedTask;
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task InterfaceImplementation_AsyncWithoutToken_NoDiagnostic()
    {
        // The interface method declaration (without CancellationToken) triggers BW1001
        // because the interface author controls that signature. The class implementation
        // is an implicit interface implementation and does NOT trigger BW1001.
        var source = """
            using System.Threading.Tasks;
            public interface IHandler
            {
                Task {|BW1001:HandleAsync|}();
            }
            public class Handler : IHandler
            {
                public Task HandleAsync() => Task.CompletedTask;
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task ExplicitInterfaceImplementation_AsyncWithoutToken_NoDiagnostic()
    {
        // Explicit interface implementation is also excluded from BW1001.
        // The interface declaration itself triggers BW1001; the explicit implementation does not.
        var source = """
            using System.Threading.Tasks;
            public interface IRunner
            {
                Task {|BW1001:RunAsync|}();
            }
            public class Runner : IRunner
            {
                Task IRunner.RunAsync() => Task.CompletedTask;
            }
            """;

        await CSharpAnalyzerVerifier<CancellationTokenPropagationAnalyzer>
            .VerifyAnalyzerAsync(source);
    }
}
