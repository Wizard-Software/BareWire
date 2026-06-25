using AwesomeAssertions;
using BareWire.Transport.RabbitMQ.Internal;

// Helper types in explicit namespaces so that Namespace:TypeName strings are deterministic.
namespace OrderSystem.Events
{
    public sealed record OrderSubmitted;
}

namespace A.B.C
{
    public sealed record DeepNamespaceEvent;
}

namespace BareWire.UnitTests.Transport.RabbitMq
{
    public sealed class RequestExchangeNameFormatterTests
    {
        [Fact]
        public void Format_ForSimpleType_ReturnsNamespaceColonTypeName()
        {
            string result = RequestExchangeNameFormatter.Format<global::OrderSystem.Events.OrderSubmitted>();

            result.Should().Be("OrderSystem.Events:OrderSubmitted");
        }

        [Fact]
        public void Format_PreservesDotsInsideNamespace_AndPascalCaseTypeName()
        {
            string result = RequestExchangeNameFormatter.Format<global::A.B.C.DeepNamespaceEvent>();

            result.Should().Be("A.B.C:DeepNamespaceEvent");
        }

        [Fact]
        public void Format_CalledTwiceForSameType_ReturnsSameStringInstance()
        {
            string first = RequestExchangeNameFormatter.Format<global::OrderSystem.Events.OrderSubmitted>();
            string second = RequestExchangeNameFormatter.Format<global::OrderSystem.Events.OrderSubmitted>();

            ReferenceEquals(first, second).Should().BeTrue(
                "the per-type static cache must return the same string instance on every call (zero allocation per call)");
        }
    }
}
