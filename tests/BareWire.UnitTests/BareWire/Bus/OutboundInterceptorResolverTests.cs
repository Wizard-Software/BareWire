using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.UnitTests.Core.Bus;

public sealed class OutboundInterceptorResolverTests
{
    private sealed class FirstInterceptor : IOutboundMessageInterceptor
    {
        public bool IsCapturing => false;

        public bool TryIntercept(OutboundMessage message) => false;
    }

    private sealed class SecondInterceptor : IOutboundMessageInterceptor
    {
        public bool IsCapturing => false;

        public bool TryIntercept(OutboundMessage message) => false;
    }

    private sealed class WarningCounter : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void Resolve_WhenNoneRegistered_ReturnsNull()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();

        OutboundInterceptorResolver.Resolve(provider, NullLogger.Instance).Should().BeNull();
    }

    [Fact]
    public void Resolve_WhenSingleRegistered_ReturnsItWithoutWarning()
    {
        ServiceCollection services = new();
        services.AddSingleton<IOutboundMessageInterceptor, FirstInterceptor>();
        using ServiceProvider provider = services.BuildServiceProvider();
        WarningCounter logger = new();

        OutboundInterceptorResolver.Resolve(provider, logger).Should().BeOfType<FirstInterceptor>();
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_WhenMultipleRegistered_UsesLastAndLogsWarningNamingTypes()
    {
        ServiceCollection services = new();
        services.AddSingleton<IOutboundMessageInterceptor, FirstInterceptor>();
        services.AddSingleton<IOutboundMessageInterceptor, SecondInterceptor>();
        using ServiceProvider provider = services.BuildServiceProvider();
        WarningCounter logger = new();

        OutboundInterceptorResolver.Resolve(provider, logger).Should().BeOfType<SecondInterceptor>();

        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain(nameof(FirstInterceptor)).And.Contain(nameof(SecondInterceptor));
    }
}
