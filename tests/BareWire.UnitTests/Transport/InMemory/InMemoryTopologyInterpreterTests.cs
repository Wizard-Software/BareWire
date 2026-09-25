using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using BareWire.Transport.InMemory.Topology;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryTopologyInterpreterTests
{
    private static InMemoryTransportOptions Options(Action<IInMemoryConfigurator> configure)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        return c.Build();
    }

    [Fact]
    public void BuildRegistry_DirectFanoutTopicWithBindings_RegistersAllEntities()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("direct-ex", ExchangeType.Direct);
            t.DeclareExchange("fanout-ex", ExchangeType.Fanout);
            t.DeclareExchange("topic-ex", ExchangeType.Topic);
            t.DeclareQueue("q1");
            t.DeclareQueue("q2");
            t.BindExchangeToQueue("direct-ex", "q1", "orders.created");
            t.BindExchangeToExchange("topic-ex", "fanout-ex", "orders.#");
        }));

        ExchangeRegistry r = InMemoryTopologyInterpreter.BuildRegistry(o);

        r.Exchanges.Keys.Should().BeEquivalentTo(["direct-ex", "fanout-ex", "topic-ex"]);
        r.Queues.Keys.Should().BeEquivalentTo(["q1", "q2"]);
        r.ExchangeQueueBindings.Should().ContainSingle(b => b.ExchangeName == "direct-ex" && b.QueueName == "q1");
        r.ExchangeExchangeBindings.Should().ContainSingle(
            b => b.SourceExchangeName == "topic-ex" && b.DestinationExchangeName == "fanout-ex");
    }

    [Fact]
    public void BuildRegistry_IdenticalDuplicateDeclarations_AreMerged()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("ex", ExchangeType.Direct);
            t.DeclareExchange("ex", ExchangeType.Direct);
            t.DeclareQueue("q");
            t.DeclareQueue("q");
            t.BindExchangeToQueue("ex", "q", "k");
            t.BindExchangeToQueue("ex", "q", "k");
        }));

        ExchangeRegistry r = InMemoryTopologyInterpreter.BuildRegistry(o);

        r.Exchanges.Should().ContainSingle();
        r.Queues.Should().ContainSingle();
        r.ExchangeQueueBindings.Should().ContainSingle();
    }

    [Fact]
    public void BuildRegistry_ConflictingExchangeDeclaration_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("ex", ExchangeType.Direct);
            t.DeclareExchange("ex", ExchangeType.Fanout);
        }));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*ex*");
    }

    [Fact]
    public void BuildRegistry_ConflictingQueueArguments_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareQueue("q", arguments: new Dictionary<string, object> { ["x-dead-letter-exchange"] = "dlx1" });
            t.DeclareQueue("q", arguments: new Dictionary<string, object> { ["x-dead-letter-exchange"] = "dlx2" });
        }));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*q*");
    }

    [Fact]
    public void BuildRegistry_HeadersExchange_ThrowsTransportException()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t => t.DeclareExchange("h", ExchangeType.Headers)));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireTransportException>().WithMessage("*Headers*");
    }

    [Fact]
    public void BuildRegistry_ConsistentHashExchange_ThrowsTransportException()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(
            t => t.DeclareExchange("ch", ExchangeType.ConsistentHash)));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireTransportException>().WithMessage("*ConsistentHash*");
    }

    public static TheoryData<Action<IQueueConfigurator>, string> UnsupportedQueueArgumentCases => new()
    {
        { q => q.MessageTtl(TimeSpan.FromSeconds(30)), "x-message-ttl" },
        { q => q.MaxLength(100), "x-max-length" },
        { q => q.MaxLengthBytes(1024), "x-max-length-bytes" },
        { q => q.Argument("x-expires", 60000L), "x-expires" },
    };

    [Theory]
    [MemberData(nameof(UnsupportedQueueArgumentCases))]
    public void BuildRegistry_QueueWithUnsupportedArgument_ThrowsTransportException(
        Action<IQueueConfigurator> configureQueue, string expectedKey)
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(
            t => t.DeclareQueue("q", true, false, configureQueue)));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireTransportException>().WithMessage($"*{expectedKey}*");
    }

    [Fact]
    public void BuildRegistry_QueueWithDeadLetterArguments_IsAccepted()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t => t.DeclareQueue(
            "q",
            durable: true,
            autoDelete: false,
            configure: q => q.DeadLetterExchange("dlx").DeadLetterRoutingKey("dl-key"))));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildRegistry_DefaultExchangeUndeclared_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(c => c.DefaultExchange("missing"));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*missing*");
    }

    [Fact]
    public void BuildRegistry_DefaultExchangeEmpty_IsAccepted()
    {
        InMemoryTransportOptions o = Options(c => c.DefaultExchange(string.Empty));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildRegistry_DefaultExchangeDeclared_IsAccepted()
    {
        InMemoryTransportOptions o = Options(c =>
        {
            c.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Direct));
            c.DefaultExchange("orders");
        });

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildRegistry_MapExchangeToUndeclaredExchange_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(c => c.MapExchange<OrderCreated>("missing"));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*OrderCreated*");
    }

    [Fact]
    public void BuildRegistry_PublishConfiguratorToUndeclaredExchange_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(c => c.Publish<OrderCreated>(p => p.Exchange("missing")));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*OrderCreated*");
    }

    [Fact]
    public void BuildRegistry_DeclareExchangeGeneric_MapsToDeclaredExchange_IsAccepted()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(
            t => t.DeclareExchange<OrderCreated>("orders", ExchangeType.Direct)));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildRegistry_ReceiveEndpointWithoutDeclaredQueue_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(c => c.ReceiveEndpoint("orders", _ => { }));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*orders*");
    }

    [Fact]
    public void BuildRegistry_ReceiveEndpointWithAutoDeclare_DeclaresQueueWithoutExchangesOrBindings()
    {
        InMemoryTransportOptions o = Options(c =>
        {
            c.AutoDeclareEndpointQueues();
            c.ReceiveEndpoint("orders", _ => { });
        });

        ExchangeRegistry r = InMemoryTopologyInterpreter.BuildRegistry(o);

        r.ContainsQueue("orders").Should().BeTrue();
        r.AutoDeclaredQueueNames.Should().BeEquivalentTo(["orders"]);
        r.Exchanges.Should().BeEmpty();
        r.ExchangeQueueBindings.Should().BeEmpty();
        r.ExchangeExchangeBindings.Should().BeEmpty();
    }

    [Fact]
    public void BuildRegistry_AutoDeclareWithAlreadyDeclaredQueue_KeepsExplicitDeclaration()
    {
        InMemoryTransportOptions o = Options(c =>
        {
            c.AutoDeclareEndpointQueues();
            c.ConfigureTopology(t => t.DeclareQueue("orders", durable: false));
            c.ReceiveEndpoint("orders", _ => { });
        });

        ExchangeRegistry r = InMemoryTopologyInterpreter.BuildRegistry(o);

        r.Queues["orders"].Durable.Should().BeFalse();
        r.AutoDeclaredQueueNames.Should().BeEmpty();
    }

    [Fact]
    public void BuildRegistry_BindingToUndeclaredExchange_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareQueue("q");
            t.BindExchangeToQueue("missing", "q", "k");
        }));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*missing*");
    }

    [Fact]
    public void BuildRegistry_BindingToUndeclaredQueue_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("ex", ExchangeType.Direct);
            t.BindExchangeToQueue("ex", "missing-queue", "k");
        }));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*missing-queue*");
    }

    // R-2: BindExchangeToQueue rejects an empty exchange name via ArgumentException, so the "" ->
    // default-exchange case cannot be produced through the fluent configurator. Build the
    // TopologyDeclaration by hand instead.
    [Fact]
    public void BuildRegistry_BindingFromDefaultExchange_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(_ => { });
        o.Topology = new TopologyDeclaration
        {
            Queues = [new QueueDeclaration("q")],
            ExchangeQueueBindings = [new ExchangeQueueBinding("", "q", "k")],
        };

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*q*");
    }

    [Fact]
    public void BuildRegistry_ExchangeToExchangeBindingWithUndeclaredDestination_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("source-ex", ExchangeType.Topic);
            t.BindExchangeToExchange("source-ex", "missing-destination", "k");
        }));

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*missing-destination*");
    }

    [Fact]
    public void BuildRegistry_BindingToAutoDeclaredQueue_IsAccepted()
    {
        InMemoryTransportOptions o = Options(c =>
        {
            c.AutoDeclareEndpointQueues();
            c.ReceiveEndpoint("orders", _ => { });
            c.ConfigureTopology(t =>
            {
                t.DeclareExchange("ex", ExchangeType.Direct);
                t.BindExchangeToQueue("ex", "orders", "k");
            });
        });

        ExchangeRegistry r = InMemoryTopologyInterpreter.BuildRegistry(o);

        r.ExchangeQueueBindings.Should().ContainSingle(b => b.QueueName == "orders");
    }

    // R-2: DeclareExchange("") also throws via ArgumentException on the fluent configurator; build
    // the TopologyDeclaration by hand to exercise the interpreter's own empty-name guard.
    [Fact]
    public void BuildRegistry_ExchangeNamedEmpty_ThrowsConfigurationException()
    {
        InMemoryTransportOptions o = Options(_ => { });
        o.Topology = new TopologyDeclaration { Exchanges = [new ExchangeDeclaration("", ExchangeType.Direct)] };

        Action act = () => InMemoryTopologyInterpreter.BuildRegistry(o);

        act.Should().Throw<BareWireConfigurationException>();
    }

    [Fact]
    public void ContainsExchange_DefaultExchange_ReturnsTrueWithoutEntry()
    {
        InMemoryTransportOptions o = Options(_ => { });

        ExchangeRegistry r = InMemoryTopologyInterpreter.BuildRegistry(o);

        r.ContainsExchange(string.Empty).Should().BeTrue();
        r.Exchanges.Should().NotContainKey(string.Empty);
    }

    [Fact]
    public void NormalizedTopology_IsEquivalentTo_IgnoresOrderAndDuplicates()
    {
        var a = new TopologyDeclaration
        {
            Exchanges =
            [
                new ExchangeDeclaration("ex1", ExchangeType.Direct),
                new ExchangeDeclaration("ex2", ExchangeType.Fanout),
            ],
            Queues = [new QueueDeclaration("q1"), new QueueDeclaration("q1")],
            ExchangeQueueBindings = [new ExchangeQueueBinding("ex1", "q1", "k")],
        };
        var b = new TopologyDeclaration
        {
            Exchanges =
            [
                new ExchangeDeclaration("ex2", ExchangeType.Fanout),
                new ExchangeDeclaration("ex1", ExchangeType.Direct),
            ],
            Queues = [new QueueDeclaration("q1")],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding("ex1", "q1", "k"),
                new ExchangeQueueBinding("ex1", "q1", "k"),
            ],
        };

        NormalizedTopology na = InMemoryTopologyInterpreter.Normalize(a);
        NormalizedTopology nb = InMemoryTopologyInterpreter.Normalize(b);

        na.IsEquivalentTo(nb).Should().BeTrue();
    }

    [Fact]
    public void NormalizedTopology_IsEquivalentTo_DetectsAddedBindingAndChangedType()
    {
        var baseline = new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration("ex", ExchangeType.Direct)],
            Queues = [new QueueDeclaration("q")],
            ExchangeQueueBindings = [new ExchangeQueueBinding("ex", "q", "k")],
        };
        TopologyDeclaration addedBinding = baseline with
        {
            ExchangeQueueBindings = [.. baseline.ExchangeQueueBindings, new ExchangeQueueBinding("ex", "q", "other")],
        };
        TopologyDeclaration changedType = baseline with
        {
            Exchanges = [new ExchangeDeclaration("ex", ExchangeType.Fanout)],
        };

        NormalizedTopology nBaseline = InMemoryTopologyInterpreter.Normalize(baseline);

        InMemoryTopologyInterpreter.Normalize(addedBinding).IsEquivalentTo(nBaseline).Should().BeFalse();
        InMemoryTopologyInterpreter.Normalize(changedType).IsEquivalentTo(nBaseline).Should().BeFalse();
    }

    // R-3: ReceiveEndpoint does not reject duplicate queue names, so BuildRegistry must consolidate
    // unique endpoint queue names before checking declarations / auto-declaring — otherwise adding the
    // same auto-declared QueueDeclaration twice blows up the registry's dictionary construction.
    [Fact]
    public void BuildRegistry_DuplicateEndpointNamesWithAutoDeclare_DeclaresQueueOnce()
    {
        InMemoryTransportOptions o = Options(c =>
        {
            c.AutoDeclareEndpointQueues();
            c.ReceiveEndpoint("orders", _ => { });
            c.ReceiveEndpoint("orders", _ => { });
        });

        ExchangeRegistry r = InMemoryTopologyInterpreter.BuildRegistry(o);

        r.ContainsQueue("orders").Should().BeTrue();
        r.AutoDeclaredQueueNames.Should().BeEquivalentTo(["orders"]);
        r.Queues.Should().ContainSingle();
    }

    // R-4: the registry must copy queue arguments at build time — a mutation of the caller's source
    // dictionary after BuildRegistry returns must not be visible through the sealed registry.
    [Fact]
    public void BuildRegistry_MutatingSourceArgumentsAfterBuild_DoesNotChangeRegistry()
    {
        var arguments = new Dictionary<string, object> { ["x-custom"] = "before" };
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q", arguments: arguments)));

        ExchangeRegistry r = InMemoryTopologyInterpreter.BuildRegistry(o);
        arguments["x-custom"] = "after";

        r.Queues["q"].Arguments!["x-custom"].Should().Be("before");
    }

    private sealed record OrderCreated;
}
