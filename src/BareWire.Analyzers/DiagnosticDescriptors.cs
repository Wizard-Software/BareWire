using Microsoft.CodeAnalysis;

namespace BareWire.Analyzers;

/// <summary>
/// Centralized repository of all <see cref="DiagnosticDescriptor"/> instances used by BareWire analyzers.
/// </summary>
public static class DiagnosticDescriptors
{
    /// <summary>Diagnostic ID for the CancellationToken propagation rule.</summary>
    public const string CancellationTokenPropagationId = "BW1001";

    /// <summary>
    /// BW1001 — every public async method must accept <c>CancellationToken</c> as its last parameter.
    /// </summary>
    public static readonly DiagnosticDescriptor CancellationTokenPropagation = new(
        id: CancellationTokenPropagationId,
        title: "Public async method should accept CancellationToken as the last parameter",
        messageFormat: "Public async method '{0}' should accept CancellationToken as the last parameter",
        category: "BareWire.Async",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Per CONSTITUTION.md, public async methods must propagate CancellationToken as their last parameter (defaulting to default).");
}
