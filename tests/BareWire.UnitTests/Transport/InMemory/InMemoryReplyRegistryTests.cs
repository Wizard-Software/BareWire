using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryReplyRegistryTests
{
    private static InMemoryReplyRegistry NewRegistry() => new(NullLogger<InMemoryReplyRegistry>.Instance);

    private sealed class CapturingSink(bool canAccept, bool accept) : IInMemoryReplySink
    {
        internal InboundMessage? Received { get; private set; }

        public bool CanAccept(IReadOnlyDictionary<string, string> headers) => canAccept;

        public bool TryAccept(InboundMessage response)
        {
            Received = response;
            return accept;
        }
    }

    [Fact]
    public void TryDeliver_WhenSinkAccepts_DeliversCopyWithoutBwHeaders()
    {
        InMemoryReplyRegistry registry = NewRegistry();
        var sink = new CapturingSink(canAccept: true, accept: true);
        string address = registry.Register(sink);
        var headers = new Dictionary<string, string>
        {
            ["BW-Exchange"] = "",
            ["bw-routingkey"] = "x",
            ["BW-MessageType"] = "urn:message:Pong",
            ["correlation-id"] = "c1",
        };

        ReplyDeliveryResult result = registry.TryDeliver(
            address, headers, "abc"u8, "application/json", new InMemoryBufferPool(null));

        result.Should().Be(ReplyDeliveryResult.Delivered);
        address.Should().StartWith(InMemoryReplyRegistry.ReservedPrefix);
        sink.Received!.Headers.Should().NotContainKey("BW-Exchange");
        sink.Received.Headers.Should().NotContainKey("bw-routingkey");
        sink.Received.Headers["BW-MessageType"].Should().Be("urn:message:Pong");
        sink.Received.Headers["correlation-id"].Should().Be("c1");
        sink.Received.Headers["content-type"].Should().Be("application/json");
        sink.Received.Headers["message-id"].Should().NotBeNullOrEmpty();
        sink.Received.Body.ToArray().Should().Equal("abc"u8.ToArray());
    }

    [Fact]
    public void TryDeliver_WhenContentTypeEmpty_KeepsPublisherContentTypeHeader()
    {
        InMemoryReplyRegistry registry = NewRegistry();
        var sink = new CapturingSink(canAccept: true, accept: true);
        string address = registry.Register(sink);
        var headers = new Dictionary<string, string> { ["content-type"] = "text/plain", ["message-id"] = "m-1" };

        registry.TryDeliver(address, headers, "a"u8, "", new InMemoryBufferPool(null));

        sink.Received!.Headers["content-type"].Should().Be("text/plain");
        sink.Received.Headers["message-id"].Should().Be("m-1");
        sink.Received.MessageId.Should().Be("m-1");
    }

    [Fact]
    public void TryDeliver_WhenSinkRejectsAfterCopy_ReturnsUnknownCorrelationAndReturnsBuffer()
    {
        InMemoryReplyRegistry registry = NewRegistry();
        var sink = new CapturingSink(canAccept: true, accept: false);
        string address = registry.Register(sink);
        var observer = new CountingBufferPoolObserver();

        ReplyDeliveryResult result = registry.TryDeliver(
            address, new Dictionary<string, string>(), "abc"u8, "application/json", new InMemoryBufferPool(observer));

        result.Should().Be(ReplyDeliveryResult.UnknownCorrelation);
        observer.Rented.Should().Be(1);
        sink.Received!.PooledBuffer.Should().BeNull("the registry disposed the rejected copy");
        observer.Outstanding.Should().Be(1, "InboundMessage.Dispose bypasses the pool observer");
        observer.MarkReturnedByMessage(observer.OutstandingBuffers().Single());
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
    }

    [Fact]
    public void TryDeliver_WhenCorrelationDoesNotMatch_ReturnsUnknownCorrelationWithoutRenting()
    {
        InMemoryReplyRegistry registry = NewRegistry();
        string address = registry.Register(new CapturingSink(canAccept: false, accept: true));
        var observer = new CountingBufferPoolObserver();

        ReplyDeliveryResult result = registry.TryDeliver(
            address, new Dictionary<string, string>(), "abc"u8, "", new InMemoryBufferPool(observer));

        result.Should().Be(ReplyDeliveryResult.UnknownCorrelation);
        observer.Rented.Should().Be(0);
    }

    [Fact]
    public void TryDeliver_WhenAddressUnregistered_ReturnsUnknownAddressWithoutRenting()
    {
        InMemoryReplyRegistry registry = NewRegistry();
        var observer = new CountingBufferPoolObserver();

        ReplyDeliveryResult result = registry.TryDeliver(
            "bw.reply.00000000000000000000000000000000", new Dictionary<string, string>(), "abc"u8, "",
            new InMemoryBufferPool(observer));

        result.Should().Be(ReplyDeliveryResult.UnknownAddress);
        observer.Rented.Should().Be(0);
    }

    [Fact]
    public void TryDeliver_WhenSinkThrows_DisposesCopyAndRethrows()
    {
        InMemoryReplyRegistry registry = NewRegistry();
        string address = registry.Register(new ThrowingSink());
        var observer = new CountingBufferPoolObserver();

        Action act = () => registry.TryDeliver(
            address, new Dictionary<string, string>(), "abc"u8, "", new InMemoryBufferPool(observer));

        act.Should().Throw<InvalidOperationException>();
        observer.Rented.Should().Be(1);
    }

    [Fact]
    public void Unregister_WhenRegistered_RemovesAddress()
    {
        InMemoryReplyRegistry registry = NewRegistry();
        string address = registry.Register(new CapturingSink(true, true));
        registry.Count.Should().Be(1);

        registry.Unregister(address).Should().BeTrue();

        registry.Count.Should().Be(0);
        registry.Unregister(address).Should().BeFalse();
    }

    [Fact]
    public void IsReplyAddress_OnlyForDefaultExchangeAndStrictFormat_ReturnsExpected()
    {
        string valid = InMemoryReplyRegistry.ReservedPrefix + new string('a', 32);

        InMemoryReplyRegistry.IsReplyAddress("", valid).Should().BeTrue();
        InMemoryReplyRegistry.IsReplyAddress("", InMemoryReplyRegistry.ReservedPrefix + "0123456789abcdef0123456789abcdef").Should().BeTrue();
        InMemoryReplyRegistry.IsReplyAddress("orders", valid).Should().BeFalse();
        InMemoryReplyRegistry.IsReplyAddress("", valid.ToUpperInvariant()).Should().BeFalse();
        InMemoryReplyRegistry.IsReplyAddress("", "BW.REPLY." + new string('a', 32)).Should().BeFalse();
        InMemoryReplyRegistry.IsReplyAddress("", "ping").Should().BeFalse();
        InMemoryReplyRegistry.IsReplyAddress("", "bw.reply.x").Should().BeFalse();
        InMemoryReplyRegistry.IsReplyAddress("", valid + "a").Should().BeFalse();
        InMemoryReplyRegistry.IsReplyAddress("", valid[..^1]).Should().BeFalse();
        InMemoryReplyRegistry.IsReplyAddress("", InMemoryReplyRegistry.ReservedPrefix + new string('g', 32)).Should().BeFalse();
        InMemoryReplyRegistry.IsReplyAddress("", InMemoryReplyRegistry.ReservedPrefix + new string('A', 32)).Should().BeFalse();
    }

    [Fact]
    public void Register_WhenCalled_ProducesAddressRecognizedAsReplyAddress()
    {
        InMemoryReplyRegistry registry = NewRegistry();

        string address = registry.Register(new CapturingSink(true, true));

        InMemoryReplyRegistry.IsReplyAddress("", address).Should().BeTrue();
    }

    [Theory]
    [InlineData("bw.reply.mine", true)]
    [InlineData("bw.reply.", true)]
    [InlineData("BW.REPLY.mine", false)]
    [InlineData("orders", false)]
    public void IsReservedName_OrdinalPrefix_ReturnsExpected(string name, bool expected) =>
        InMemoryReplyRegistry.IsReservedName(name).Should().Be(expected);

    [Fact]
    public void InMemoryPooledWriter_WhenGrowingPastInitialCapacity_PreservesWrittenBytes()
    {
        byte[] input = new byte[10_000];
        new Random(42).NextBytes(input);
        using var writer = new InMemoryPooledWriter(16);

        int offset = 0;
        while (offset < input.Length)
        {
            int chunk = Math.Min(37, input.Length - offset);
            Span<byte> span = writer.GetSpan(chunk);
            input.AsSpan(offset, chunk).CopyTo(span);
            writer.Advance(chunk);
            offset += chunk;
        }

        writer.WrittenMemory.ToArray().Should().Equal(input);
    }

    [Fact]
    public void InMemoryPooledWriter_WhenDisposedTwice_DoesNotThrow()
    {
        var writer = new InMemoryPooledWriter();
        writer.GetSpan(1)[0] = 1;
        writer.Advance(1);

        writer.Dispose();
        Action act = writer.Dispose;

        act.Should().NotThrow();
    }

    private sealed class ThrowingSink : IInMemoryReplySink
    {
        public bool CanAccept(IReadOnlyDictionary<string, string> headers) => true;

        public bool TryAccept(InboundMessage response) => throw new InvalidOperationException("boom");
    }
}
