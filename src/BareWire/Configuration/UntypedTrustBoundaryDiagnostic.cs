using BareWire.Abstractions.Configuration;
using Microsoft.Extensions.Logging;

namespace BareWire.Configuration;

/// <summary>
/// Startup advisory diagnostic for the type-less foreign-input trust boundary (ADR-030 §Security,
/// SEC-13). A receive endpoint that exposes the type-less layer — at least one consumer declared
/// <c>AcceptUntyped()</c> — becomes a sink for unauthenticated, producer-controlled foreign JSON
/// selected purely by routing-key pattern match. Foreign-input validation (a schema-validation
/// middleware) is effectively required on such endpoints, so this diagnostic emits a warning when an
/// <c>AcceptUntyped()</c> endpoint is configured without any schema-validation middleware registered.
/// </summary>
/// <remarks>
/// <para>
/// Routing-key pattern matching is performed <strong>client-side at dispatch</strong> and is a
/// dispatcher predicate, <strong>not</strong> an authorization mechanism. The trust boundary assumes
/// broker-level publish ACLs are enforced; the diagnostic is advisory (a warning, never a hard
/// failure) so configurations that validate foreign input by another mechanism are not blocked.
/// </para>
/// <para>
/// Mirrors the conditional-warning pattern of the outbox dialect-mismatch checker: a small,
/// logger-driven diagnostic that is unit-testable in isolation. The raw <c>BW-RoutingKey</c> is never
/// logged (none exists at configuration time) — the warning carries only the endpoint name.
/// </para>
/// <para>
/// A second advisory (task 18.7 / D5) covers the per-consumer MassTransit-envelope axis: a consumer
/// combining <c>UseMassTransitEnvelope()</c> with <c>AcceptUntyped()</c> deserializes an
/// unauthenticated, producer-controlled foreign MassTransit envelope AND is selected by routing-key
/// match alone. That combination, absent a schema validator, gets its own warning. A consumer that
/// opts into the MT envelope WITHOUT <c>AcceptUntyped()</c> has a narrower boundary (its message type
/// is declared, not attacker-chosen) and does not trigger the MT advisory.
/// </para>
/// <para>
/// Both advisories share the same bus-global suppression heuristic: a registered middleware whose
/// type name contains <c>SchemaValidation</c> is treated as the foreign-input validator and silences
/// the warnings. For the MT-envelope axis this assumes the named validator also covers the MT
/// envelope shape — a possible advisory false-negative (SEC-3). The diagnostic is advisory only
/// (never enforces), so this heuristic cannot cause a security regression; the operator remains
/// responsible for ensuring the validator covers envelope-wrapped foreign input.
/// </para>
/// </remarks>
internal static partial class UntypedTrustBoundaryDiagnostic
{
    /// <summary>
    /// Inspects the supplied <paramref name="configurator"/> and emits one advisory warning per receive
    /// endpoint that declares <c>AcceptUntyped()</c> while no schema-validation middleware is registered.
    /// </summary>
    /// <param name="configurator">The bus configurator whose endpoints and middleware are inspected.</param>
    /// <param name="logger">The logger that receives the advisory warnings.</param>
    internal static void Run(BusConfigurator configurator, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(logger);

        // When a schema-validation middleware is registered it applies to every endpoint on the bus
        // (middleware is bus-global), so foreign-input validation is present and no advisory is needed.
        if (HasSchemaValidationMiddleware(configurator))
        {
            return;
        }

        foreach (ReceiveEndpointConfiguration endpoint in configurator.ReceiveEndpoints)
        {
            if (EndpointAcceptsUntyped(endpoint))
            {
                LogUntypedWithoutSchemaValidation(logger, endpoint.EndpointName);
            }

            // D5 (task 18.7): the per-consumer MassTransit-envelope axis. Emitted once per endpoint
            // (not per consumer) to keep advisory noise bounded.
            if (EndpointHasMtEnvelopeUntypedConsumer(endpoint))
            {
                LogMtEnvelopeUntypedWithoutSchemaValidation(logger, endpoint.EndpointName);
            }
        }
    }

    private static bool EndpointHasMtEnvelopeUntypedConsumer(ReceiveEndpointConfiguration endpoint)
    {
        IReadOnlyList<ConsumerRegistration> registrations = endpoint.ConsumerRegistrations;
        for (int i = 0; i < registrations.Count; i++)
        {
            // The advisory targets the COMBINATION: an MT-enveloped consumer that also accepts
            // untyped foreign input. UseMassTransitEnvelope() alone (typed) is a narrower boundary.
            if (registrations[i].UseMassTransitEnvelope && registrations[i].AcceptUntyped)
            {
                return true;
            }
        }

        return false;
    }

    private static bool EndpointAcceptsUntyped(ReceiveEndpointConfiguration endpoint)
    {
        IReadOnlyList<ConsumerRegistration> registrations = endpoint.ConsumerRegistrations;
        for (int i = 0; i < registrations.Count; i++)
        {
            if (registrations[i].AcceptUntyped)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSchemaValidationMiddleware(BusConfigurator configurator)
    {
        IReadOnlyList<Type> middlewareTypes = configurator.MiddlewareTypes;
        for (int i = 0; i < middlewareTypes.Count; i++)
        {
            // Name-convention detection: the SEC-13 documented example is named SchemaValidationMiddleware.
            // A registered middleware whose type name contains "SchemaValidation" is treated as the
            // foreign-input schema validator. This keeps the diagnostic free of any new public marker type.
            if (middlewareTypes[i].Name.Contains("SchemaValidation", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Receive endpoint '{EndpointName}' declares AcceptUntyped() (type-less foreign-input " +
                  "trust boundary) but no schema-validation middleware is registered. Routing-key pattern " +
                  "matching is client-side dispatch, NOT authorization; foreign-input validation (routing " +
                  "key + broker identity + payload shape/size) is effectively required. Register a " +
                  "schema-validation middleware via AddMiddleware<...>() and ensure broker-level publish " +
                  "ACLs are enforced.")]
    private static partial void LogUntypedWithoutSchemaValidation(ILogger logger, string endpointName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Receive endpoint '{EndpointName}' has a consumer combining UseMassTransitEnvelope() " +
                  "with AcceptUntyped() but no schema-validation middleware is registered. The MassTransit " +
                  "envelope is unauthenticated, producer-controlled foreign input; combined with type-less " +
                  "acceptance (routing-key dispatch, NOT authorization) foreign-input validation (envelope " +
                  "shape + payload size/depth) is effectively required. Register a schema-validation " +
                  "middleware via AddMiddleware<...>() and ensure broker-level publish ACLs are enforced.")]
    private static partial void LogMtEnvelopeUntypedWithoutSchemaValidation(ILogger logger, string endpointName);
}
