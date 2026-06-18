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
        title: "Publiczna metoda async powinna przyjmować CancellationToken jako ostatni parametr",
        messageFormat: "Publiczna metoda async '{0}' powinna przyjmować CancellationToken jako ostatni parametr",
        category: "BareWire.Async",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Zgodnie z CONSTITUTION.md, publiczne metody async muszą propagować CancellationToken jako ostatni parametr (domyślnie default).");
}
