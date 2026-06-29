using AwesomeAssertions;
using BareWire.Abstractions;
using Xunit;

namespace BareWire.UnitTests.Abstractions;

/// <summary>
/// Contract guard for the public <see cref="IRequestClient{TRequest}"/> surface (issue #41).
/// </summary>
public sealed class RequestClientContractTests
{
    // A minimal reference-type request, satisfying the `where TRequest : class` constraint.
    private sealed record Probe;

    [Fact]
    public void IRequestClient_ShouldExtend_IAsyncDisposable()
    {
        // Callers obtain IRequestClient<T> via IBus.CreateRequestClientAsync. The RabbitMQ
        // implementation holds an exclusive auto-delete response queue (plus a channel and a
        // consumer) that only goes away when the client is disposed or the shared connection
        // closes. Without IAsyncDisposable on the contract, callers get no compile-time signal
        // to dispose and cannot `await using` it — temporary queues pile up between restarts.
        typeof(IAsyncDisposable)
            .IsAssignableFrom(typeof(IRequestClient<Probe>))
            .Should()
            .BeTrue(
                because: "IRequestClient<T> owns disposable transport resources and must surface " +
                         "IAsyncDisposable on its public contract so callers can 'await using' it (issue #41)");
    }
}
