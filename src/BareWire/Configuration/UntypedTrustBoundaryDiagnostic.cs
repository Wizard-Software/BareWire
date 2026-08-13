using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Topology;
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
/// <para>
/// A third advisory (task 19.11) covers the topology axis: an <c>AcceptUntyped()</c> endpoint
/// combined with the bus declaring <strong>any</strong> opt-in topology (via
/// <see cref="Run(IReadOnlyList{EndpointBinding}, TopologyDeclaration?, BusConfigurator, ILogger)"/>'s
/// <c>optInTopology</c> parameter). The underlying signal — a non-null bus-global
/// <c>TopologyDeclaration</c> — is <strong>not</strong> scoped to consumer topology alone: it also goes
/// non-null for publish-side <c>ConfigureTopology</c>/AutoDeclare exchange declarations, so this
/// axis deliberately over-warns (fires for any AcceptUntyped endpoint whenever any opt-in topology
/// exists anywhere on the bus, not only when the SPECIFIC endpoint opted into consumer topology).
/// This is a conservative, advisory-only trade-off: a benign false positive is preferred over a
/// false negative, and no more precise per-binding topology signal exists without new plumbing
/// (out of scope for this task). The warning wording reflects this by referring to "opt-in topology
/// (publish or consumer)" rather than claiming the endpoint itself declared consumer topology.
/// </para>
/// <para>
/// Deserialization-hardening parity (task 19.11): this diagnostic only advises registering a
/// schema-validation middleware — it does not itself perform hardening. Parity with the type-less
/// path (ADR-031) is met by hardening that already exists elsewhere on the raw-deserialization
/// route: a maximum payload-size limit is enforced before deserialization, the default message
/// serializer never registers a polymorphic <c>TypeInfoResolver</c>, and <c>System.Text.Json</c>'s
/// default <c>MaxDepth</c> bounds nesting. The advisory is a documentation/detection layer over
/// guarantees that already hold, not a substitute for them.
/// </para>
/// <para>
/// Broker-level publish ACL assumption (reaffirmed for task 19.11): every axis above assumes the
/// message broker enforces publish authorization at the exchange/topic level. Routing-key pattern
/// matching performed by this library is dispatch-only and never a substitute for that ACL.
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

    /// <summary>
    /// Inspects the supplied MATERIALIZED <paramref name="bindings"/> — <see cref="EndpointBinding"/>
    /// instances whose <see cref="EndpointBinding.Consumers"/> carry <c>ConsumerDefinition&lt;T&gt;</c>-merged
    /// flags (task 19.8/19.9) — together with the bus-global <paramref name="optInTopology"/>, and emits one
    /// advisory warning per axis that is exposed without a registered schema-validation middleware.
    /// </summary>
    /// <param name="bindings">
    /// The materialized endpoint bindings whose consumer registrations are inspected. Unlike
    /// <see cref="Run(BusConfigurator, ILogger)"/>, this overload reads flags AFTER definition-merging, so a
    /// consumer that only becomes <c>AcceptUntyped</c>/<c>UseMassTransitEnvelope</c> through a
    /// <c>ConsumerDefinition&lt;T&gt;</c> (invisible in <c>configurator.ReceiveEndpoints</c>) is correctly seen.
    /// </param>
    /// <param name="optInTopology">
    /// The bus-global <see cref="TopologyDeclaration"/> resolved at startup, or <see langword="null"/> when no
    /// topology was declared. A non-null value drives the topology axis (see remarks for the conservative
    /// over-warn: this signal is bus-global, not per-endpoint).
    /// </param>
    /// <param name="configurator">The bus configurator whose middleware is inspected (suppression only).</param>
    /// <param name="logger">The logger that receives the advisory warnings.</param>
    internal static void Run(
        IReadOnlyList<EndpointBinding> bindings,
        TopologyDeclaration? optInTopology,
        BusConfigurator configurator,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(logger);

        // When a schema-validation middleware is registered it applies to every endpoint on the bus
        // (middleware is bus-global), so foreign-input validation is present and no advisory is needed.
        if (HasSchemaValidationMiddleware(configurator))
        {
            return;
        }

        // 19.9 merges every opt-in consumer-topology fragment into this single bus-global declaration,
        // so an active consumer opt-in guarantees optInTopology is non-null (no false negative). The
        // same field also goes non-null for publish-side ConfigureTopology/AutoDeclare — see remarks.
        bool optInTopologyPresent = optInTopology is not null;

        foreach (EndpointBinding binding in bindings)
        {
            bool acceptsUntyped = BindingAcceptsUntyped(binding);

            if (acceptsUntyped)
            {
                LogUntypedWithoutSchemaValidation(logger, binding.EndpointName);
            }

            // D5 (task 18.7): the per-consumer MassTransit-envelope axis. Emitted once per endpoint
            // (not per consumer) to keep advisory noise bounded.
            if (BindingHasMtEnvelopeUntypedConsumer(binding))
            {
                LogMtEnvelopeUntypedWithoutSchemaValidation(logger, binding.EndpointName);
            }

            // Task 19.11: the topology axis. Conservative bus-global signal (see remarks) — fires for
            // every AcceptUntyped endpoint when ANY opt-in topology exists anywhere on the bus.
            if (optInTopologyPresent && acceptsUntyped)
            {
                LogUntypedTopologyWithoutSchemaValidation(logger, binding.EndpointName);
            }
        }
    }

    private static bool BindingAcceptsUntyped(EndpointBinding binding)
    {
        IReadOnlyList<ConsumerRegistration> consumers = binding.Consumers;
        for (int i = 0; i < consumers.Count; i++)
        {
            if (consumers[i].AcceptUntyped)
            {
                return true;
            }
        }

        return false;
    }

    private static bool BindingHasMtEnvelopeUntypedConsumer(EndpointBinding binding)
    {
        IReadOnlyList<ConsumerRegistration> consumers = binding.Consumers;
        for (int i = 0; i < consumers.Count; i++)
        {
            // The advisory targets the COMBINATION: an MT-enveloped consumer that also accepts
            // untyped foreign input. UseMassTransitEnvelope() alone (typed) is a narrower boundary.
            if (consumers[i].UseMassTransitEnvelope && consumers[i].AcceptUntyped)
            {
                return true;
            }
        }

        return false;
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Receive endpoint '{EndpointName}' declares AcceptUntyped() and the bus declares opt-in " +
                  "topology (publish or consumer) but no schema-validation middleware is registered. An " +
                  "AcceptUntyped() endpoint is a type-less foreign-input sink selected by routing-key " +
                  "pattern match alone (client-side dispatch, NOT authorization); combined with any opt-in " +
                  "topology declaration on the bus, foreign-input validation is effectively required. " +
                  "Register a schema-validation middleware via AddMiddleware<...>() and ensure " +
                  "broker-level publish ACLs are enforced.")]
    private static partial void LogUntypedTopologyWithoutSchemaValidation(ILogger logger, string endpointName);
}
