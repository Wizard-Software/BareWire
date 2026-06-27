using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Pipeline;
using BareWire.Configuration;
using Microsoft.Extensions.Logging;

namespace BareWire.UnitTests.Core.Configuration;

/// <summary>
/// Unit tests for the type-less foreign-input trust-boundary startup advisory (SEC-13 / ADR-030
/// §Security). Verifies that an endpoint declaring <c>AcceptUntyped()</c> without a registered
/// schema-validation middleware produces a single advisory warning naming the endpoint, while the
/// negative cases (schema validator present, or no <c>AcceptUntyped()</c>) stay silent.
/// </summary>
public sealed class UntypedTrustBoundaryDiagnosticTests
{
    [Fact]
    public void Run_AcceptUntypedWithoutSchemaValidation_EmitsSingleWarningNamingEndpoint()
    {
        // Arrange
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddReceiveEndpoint(
            "untyped-queue",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c =>
            {
                c.RoutingKey("transfer.eu.*");
                c.AcceptUntyped();
            }));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert
        logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Warning);
        logger.Events.Single(e => e.Level == LogLevel.Warning).Message
            .Should().Contain("untyped-queue");
    }

    [Fact]
    public void Run_AcceptUntypedWithSchemaValidationMiddleware_EmitsNoWarning()
    {
        // Arrange
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddMiddleware<FakeSchemaValidationMiddleware>();
        configurator.AddReceiveEndpoint(
            "untyped-queue",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c => c.AcceptUntyped()));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert — foreign-input validation present → no advisory.
        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Run_RoutingKeysWithoutAcceptUntyped_EmitsNoWarning()
    {
        // Arrange — consumer narrows the typed dispatch path only; the type-less sink is NOT exposed.
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddReceiveEndpoint(
            "typed-queue",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c => c.RoutingKey("transfer.eu.*")));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert — secure-by-default: no AcceptUntyped() → no warning.
        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Run_PlainCatchAllConsumer_EmitsNoWarning()
    {
        // Arrange — catch-all consumer (no routing keys, no AcceptUntyped()).
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddReceiveEndpoint(
            "plain-queue",
            ep => ep.Consumer<FakeConsumer, FakeMessage>());

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert
        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Run_MultipleEndpoints_WarnsOnlyForUntypedEndpointsWithoutValidator()
    {
        // Arrange — one type-less endpoint without a validator, one plain typed endpoint.
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        // Names are chosen so neither is a substring of the other (avoids "untyped" ⊃ "typed").
        configurator.AddReceiveEndpoint(
            "orders-foreign",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c => c.AcceptUntyped()));
        configurator.AddReceiveEndpoint(
            "orders-domestic",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c => c.RoutingKey("transfer.eu.*")));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert — exactly one warning, for the type-less endpoint only.
        logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Warning);
        string warning = logger.Events.Single(e => e.Level == LogLevel.Warning).Message;
        warning.Should().Contain("orders-foreign");
        warning.Should().NotContain("orders-domestic");
    }

    [Fact]
    public void Run_AcceptUntyped_DoesNotLogAnyRoutingKeyValue()
    {
        // Arrange — the producer-controlled routing-key VALUE must never leak into the advisory
        // (decision routing-key-in-logs). Only the endpoint name is carried.
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddReceiveEndpoint(
            "untyped-queue",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c =>
            {
                c.RoutingKey("secret.tenant.payload.key");
                c.AcceptUntyped();
            }));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert
        logger.Events.Single(e => e.Level == LogLevel.Warning).Message
            .Should().NotContain("secret.tenant.payload.key");
    }

    // ── MassTransit-envelope axis (D5 — task 18.7) ───────────────────────────────

    [Fact]
    public void Run_MtEnvelopeWithAcceptUntypedWithoutSchemaValidation_EmitsMtAdvisoryNamingEndpoint()
    {
        // Arrange — a consumer combining the MassTransit-envelope opt-in with AcceptUntyped()
        // opens BOTH foreign-input axes (foreign MT envelope + type-less selection) without a
        // schema validator.
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddReceiveEndpoint(
            "mt-foreign",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c =>
            {
                c.RoutingKey("interop.eu.*");
                c.AcceptUntyped();
                c.UseMassTransitEnvelope();
            }));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert — the MT-axis advisory is present, naming the endpoint and the MassTransit envelope.
        // (The generic type-less advisory also fires for this consumer — DEC-1 additive; asserted by
        // presence of the MT message, not ContainSingle.)
        logger.Events.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("mt-foreign")
            && e.Message.Contains("MassTransit envelope"));
    }

    [Fact]
    public void Run_MtEnvelopeWithAcceptUntypedWithSchemaValidationMiddleware_EmitsNoWarning()
    {
        // Arrange — same combination, but a bus-global schema validator is registered.
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddMiddleware<FakeSchemaValidationMiddleware>();
        configurator.AddReceiveEndpoint(
            "mt-foreign",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c =>
            {
                c.AcceptUntyped();
                c.UseMassTransitEnvelope();
            }));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert — bus-global validation present → both axes suppressed, no advisory.
        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Run_MtEnvelopeWithoutAcceptUntyped_EmitsNoMtAdvisory()
    {
        // Arrange — a typed MT consumer (UseMassTransitEnvelope() WITHOUT AcceptUntyped()).
        // Type metadata is declared by the consumer, not chosen by an attacker's routing key,
        // so the trust boundary is narrower and the MT advisory must NOT fire.
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddReceiveEndpoint(
            "mt-typed",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c => c.UseMassTransitEnvelope()));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert — the advisory requires the COMBINATION; UseMassTransitEnvelope() alone stays silent.
        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Run_MtEnvelopeUntyped_DoesNotLogRoutingKeyValue()
    {
        // Arrange — the producer-controlled routing-key VALUE must never leak into ANY advisory.
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddReceiveEndpoint(
            "mt-foreign",
            ep => ep.Consumer<FakeConsumer, FakeMessage>(c =>
            {
                c.RoutingKey("secret.tenant.payload.key");
                c.AcceptUntyped();
                c.UseMassTransitEnvelope();
            }));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert — every emitted warning carries only the endpoint name, never the routing-key value.
        logger.Events.Should()
            .OnlyContain(e => e.Level != LogLevel.Warning || !e.Message.Contains("secret.tenant.payload.key"));
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    /// <summary>Minimal logger that captures emitted log events for assertion.</summary>
    private sealed class FakeLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, formatter(state, exception)));
    }

    private sealed record FakeMessage;

    private sealed class FakeConsumer : IConsumer<FakeMessage>
    {
        public Task ConsumeAsync(ConsumeContext<FakeMessage> context) => Task.CompletedTask;
    }

    /// <summary>
    /// A no-op middleware whose type name matches the schema-validation naming convention
    /// (<c>SchemaValidation</c>), standing in for a user-provided SEC-13 validator.
    /// </summary>
    private sealed class FakeSchemaValidationMiddleware : IMessageMiddleware
    {
        public Task InvokeAsync(MessageContext context, NextMiddleware nextMiddleware)
            => nextMiddleware(context);
    }
}
