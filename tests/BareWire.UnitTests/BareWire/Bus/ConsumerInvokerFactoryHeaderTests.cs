using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Bus;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// Must be public for ConsumerInvokerFactory to build a generic delegate over these types.
public sealed record HeaderTestMessage(string Value);

public sealed class HeaderTestConsumer : IConsumer<HeaderTestMessage>
{
    /// <summary>Captured context from the last invocation — set by ConsumeAsync.</summary>
    public ConsumeContext<HeaderTestMessage>? LastContext { get; private set; }

    public Task ConsumeAsync(ConsumeContext<HeaderTestMessage> context)
    {
        LastContext = context;
        return Task.CompletedTask;
    }
}

public sealed class RawHeaderTestConsumer : IRawConsumer
{
    /// <summary>Captured context from the last invocation — set by ConsumeAsync.</summary>
    public RawConsumeContext? LastContext { get; private set; }

    public Task ConsumeAsync(RawConsumeContext context)
    {
        LastContext = context;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Regression tests for the header key mismatch bug:
/// <see cref="ConsumerInvokerFactory"/> must read correlation-id from the canonical lowercase key
/// <c>"correlation-id"</c> (as stored by <c>RabbitMqHeaderMapper</c>), not the PascalCase key
/// <c>"CorrelationId"</c> that was previously used.
/// </summary>
public sealed class ConsumerInvokerFactoryHeaderTests
{
    private const string CorrelationIdKey = "correlation-id";
    private const string ConversationIdKey = "conversation-id";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (IServiceScopeFactory ScopeFactory, HeaderTestConsumer Consumer)
        BuildScopeFactory()
    {
        HeaderTestConsumer consumer = new();

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(HeaderTestConsumer)).Returns(consumer);

        return (scopeFactory, consumer);
    }

    private static (IServiceScopeFactory ScopeFactory, RawHeaderTestConsumer Consumer)
        BuildRawScopeFactory()
    {
        RawHeaderTestConsumer consumer = new();

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(RawHeaderTestConsumer)).Returns(consumer);

        return (scopeFactory, consumer);
    }

    private static IDeserializerResolver BuildDeserializerResolver()
    {
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.ContentType.Returns("application/json");
        deserializer.Deserialize<HeaderTestMessage>(Arg.Any<ReadOnlySequence<byte>>())
                    .Returns(new HeaderTestMessage("test"));
        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(Arg.Any<string?>()).Returns(deserializer);
        return resolver;
    }

    // ── Typed consumer — correlation-id header ────────────────────────────────

    [Fact]
    public async Task Create_WhenCorrelationIdHeaderPresent_PopulatesCorrelationId()
    {
        // Arrange
        Guid expectedCorrelationId = Guid.NewGuid();
        var headers = new Dictionary<string, string>
        {
            [CorrelationIdKey] = expectedCorrelationId.ToString(),
        };

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(HeaderTestConsumer), typeof(HeaderTestMessage));

        var (scopeFactory, consumer) = BuildScopeFactory();
        IDeserializerResolver deserializer = BuildDeserializerResolver();
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider send = Substitute.For<ISendEndpointProvider>();

        // Act
        await invoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers,
            Guid.NewGuid().ToString(), pub, send, deserializer, "ep", CancellationToken.None);

        // Assert
        consumer.LastContext.Should().NotBeNull();
        consumer.LastContext!.CorrelationId.Should().Be(expectedCorrelationId);
    }

    [Fact]
    public async Task Create_WhenConversationIdHeaderPresent_PopulatesConversationId()
    {
        // Arrange
        Guid expectedConversationId = Guid.NewGuid();
        var headers = new Dictionary<string, string>
        {
            [ConversationIdKey] = expectedConversationId.ToString(),
        };

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(HeaderTestConsumer), typeof(HeaderTestMessage));

        var (scopeFactory, consumer) = BuildScopeFactory();
        IDeserializerResolver deserializer = BuildDeserializerResolver();
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider send = Substitute.For<ISendEndpointProvider>();

        // Act
        await invoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers,
            Guid.NewGuid().ToString(), pub, send, deserializer, "ep", CancellationToken.None);

        // Assert
        consumer.LastContext.Should().NotBeNull();
        consumer.LastContext!.ConversationId.Should().Be(expectedConversationId);
    }

    [Fact]
    public async Task Create_WhenCorrelationIdHeaderAbsent_CorrelationIdIsNull()
    {
        // Arrange — no correlation-id header
        var headers = new Dictionary<string, string>();

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(HeaderTestConsumer), typeof(HeaderTestMessage));

        var (scopeFactory, consumer) = BuildScopeFactory();
        IDeserializerResolver deserializer = BuildDeserializerResolver();
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider send = Substitute.For<ISendEndpointProvider>();

        // Act
        await invoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers,
            Guid.NewGuid().ToString(), pub, send, deserializer, "ep", CancellationToken.None);

        // Assert
        consumer.LastContext.Should().NotBeNull();
        consumer.LastContext!.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenPascalCaseCorrelationIdHeaderUsed_CorrelationIdIsNull()
    {
        // Regression guard: the old wrong key "CorrelationId" (PascalCase) must NOT be read.
        // The canonical key is "correlation-id" (lowercase kebab-case).
        var headers = new Dictionary<string, string>
        {
            ["CorrelationId"] = Guid.NewGuid().ToString(), // wrong key — must be ignored
        };

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(HeaderTestConsumer), typeof(HeaderTestMessage));

        var (scopeFactory, consumer) = BuildScopeFactory();
        IDeserializerResolver deserializer = BuildDeserializerResolver();
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider send = Substitute.For<ISendEndpointProvider>();

        // Act
        await invoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers,
            Guid.NewGuid().ToString(), pub, send, deserializer, "ep", CancellationToken.None);

        // Assert — must be null; the factory must not pick up the PascalCase key
        consumer.LastContext.Should().NotBeNull();
        consumer.LastContext!.CorrelationId.Should().BeNull();
    }

    // ── Raw consumer — correlation-id header ──────────────────────────────────

    [Fact]
    public async Task CreateRaw_WhenCorrelationIdHeaderPresent_PopulatesCorrelationId()
    {
        // Arrange
        Guid expectedCorrelationId = Guid.NewGuid();
        var headers = new Dictionary<string, string>
        {
            [CorrelationIdKey] = expectedCorrelationId.ToString(),
        };

        ConsumerInvokerFactory.RawInvokerDelegate rawInvoker =
            ConsumerInvokerFactory.CreateRaw(typeof(RawHeaderTestConsumer));

        var (scopeFactory, consumer) = BuildRawScopeFactory();
        IDeserializerResolver deserializer = BuildDeserializerResolver();
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider send = Substitute.For<ISendEndpointProvider>();

        // Act
        await rawInvoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers,
            Guid.NewGuid().ToString(), pub, send, deserializer, CancellationToken.None);

        // Assert
        consumer.LastContext.Should().NotBeNull();
        consumer.LastContext!.CorrelationId.Should().Be(expectedCorrelationId);
    }

    [Fact]
    public async Task CreateRaw_WhenCorrelationIdHeaderAbsent_CorrelationIdIsNull()
    {
        // Arrange
        var headers = new Dictionary<string, string>();

        ConsumerInvokerFactory.RawInvokerDelegate rawInvoker =
            ConsumerInvokerFactory.CreateRaw(typeof(RawHeaderTestConsumer));

        var (scopeFactory, consumer) = BuildRawScopeFactory();
        IDeserializerResolver deserializer = BuildDeserializerResolver();
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider send = Substitute.For<ISendEndpointProvider>();

        // Act
        await rawInvoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers,
            Guid.NewGuid().ToString(), pub, send, deserializer, CancellationToken.None);

        // Assert
        consumer.LastContext.Should().NotBeNull();
        consumer.LastContext!.CorrelationId.Should().BeNull();
    }

    // ── Null content-type — resolver called with null ─────────────────────────

    [Fact]
    public async Task Create_WhenContentTypeHeaderAbsent_ResolvesDeserializerWithNull()
    {
        // Arrange — headers contain no "content-type" key
        var headers = new Dictionary<string, string>();

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(HeaderTestConsumer), typeof(HeaderTestMessage));

        var (scopeFactory, _) = BuildScopeFactory();

        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.ContentType.Returns("application/json");
        deserializer.Deserialize<HeaderTestMessage>(Arg.Any<ReadOnlySequence<byte>>())
                    .Returns(new HeaderTestMessage("test"));

        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(Arg.Any<string?>()).Returns(deserializer);

        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider send = Substitute.For<ISendEndpointProvider>();

        // Act
        await invoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers,
            Guid.NewGuid().ToString(), pub, send, resolver, "ep", CancellationToken.None);

        // Assert — resolver must have been called exactly once with null (no content-type in headers)
        resolver.Received(1).Resolve(null);
    }

    [Fact]
    public async Task CreateRaw_WhenContentTypeHeaderAbsent_ResolvesDeserializerWithNull()
    {
        // Arrange — headers contain no "content-type" key
        var headers = new Dictionary<string, string>();

        ConsumerInvokerFactory.RawInvokerDelegate rawInvoker =
            ConsumerInvokerFactory.CreateRaw(typeof(RawHeaderTestConsumer));

        var (scopeFactory, _) = BuildRawScopeFactory();

        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.ContentType.Returns("application/json");

        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(Arg.Any<string?>()).Returns(deserializer);

        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider send = Substitute.For<ISendEndpointProvider>();

        // Act
        await rawInvoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers,
            Guid.NewGuid().ToString(), pub, send, resolver, CancellationToken.None);

        // Assert — resolver must have been called exactly once with null (no content-type in headers)
        resolver.Received(1).Resolve(null);
    }
}
