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
        }
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
}
