using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using BareWire.Buffers;                       // PooledBufferWriter
using BareWire.Interop.MassTransit;           // MassTransitEnvelopeSerializer
using BareWire.Transport.RabbitMQ.Internal;   // RequestExchangeNameFormatter (internal via InternalsVisibleTo)
using BareWire.UnitTests.Serialization;       // NestedMessage / InnerData

namespace BareWire.UnitTests.Interop;

/// <summary>
/// Parity tests proving that the request-exchange name produced by
/// <see cref="RequestExchangeNameFormatter.Format{T}"/> (Transport.RabbitMQ, reimplemented
/// locally per the layer rule — no dependency on Interop.MassTransit) is byte-identical to
/// the MassTransit <c>urn:message:{Namespace}:{Name}</c> convention after stripping the
/// <c>urn:message:</c> prefix.
/// <para>
/// The MassTransit URN is obtained from the real production code path — the
/// <c>messageType[0]</c> element of the envelope emitted by
/// <see cref="MassTransitEnvelopeSerializer"/> — rather than from a duplicated formula.
/// (<c>UrnCache&lt;T&gt;</c> is <see langword="private"/> inside the serializer and is not
/// reachable via <c>InternalsVisibleTo</c>; deriving the URN from the serialized envelope is
/// both reachable and a stronger guarantee, since it pins parity against the actual emitted bytes.)
/// </para>
/// <para>
/// Guards against silent divergence of the naming convention between the two assemblies
/// (m7 in ADR-027): the test fails if the formatter ever used <c>.</c> instead of <c>:</c>
/// or kebab-case.
/// </para>
/// </summary>
public sealed class RequestExchangeNameParityTests
{
    private const string UrnPrefix = "urn:message:";

    private readonly MassTransitEnvelopeSerializer _serializer = new();

    private string ExtractUrnSuffix<T>(T message)
        where T : class
    {
        using var buffer = new PooledBufferWriter();
        _serializer.Serialize(message, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);

        string urn = doc.RootElement.GetProperty("messageType")[0].GetString()!;
        urn.Should().StartWith(UrnPrefix);

        return urn[UrnPrefix.Length..];
    }

    [Fact]
    public void Format_ForTestOrder_MatchesMassTransitUrnSuffix()
    {
        var message = new TestOrder("ORD-1", 1.00m);
        string urnSuffix = ExtractUrnSuffix(message);

        RequestExchangeNameFormatter.Format<TestOrder>().Should().Be(urnSuffix);
    }

    [Fact]
    public void Format_ForNestedMessage_MatchesMassTransitUrnSuffix()
    {
        var message = new NestedMessage("n", new InnerData(7, "inner-desc"));
        string urnSuffix = ExtractUrnSuffix(message);

        RequestExchangeNameFormatter.Format<NestedMessage>().Should().Be(urnSuffix);
    }

    [Fact]
    public void Format_UsesColonSeparator_NotDotOrKebab()
    {
        // Explicit assertion: the formatter uses ':' (not '.' nor kebab-case) — guards the
        // separator convention against regression independently of the serializer round-trip.
        string formatted = RequestExchangeNameFormatter.Format<TestOrder>();

        formatted.Should().Be($"{typeof(TestOrder).Namespace}:{typeof(TestOrder).Name}");
        formatted.Should().Contain(":").And.NotContain("-");
    }
}
