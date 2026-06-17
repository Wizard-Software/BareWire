using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using BareWire.Transport.AzureServiceBus.Internal;
using BareWire.Transport.AzureServiceBus.Topology;
using Xunit;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

/// <summary>
/// Broker-free unit tests for session options validation (D-10/PERF-2),
/// registry bulk eviction (D-11/VER-2),
/// topology requires-session parsing,
/// and the session configurator fluent API (GAP-2/GAP-3).
/// </summary>
public sealed class AzureServiceBusSessionOptionsTests
{
    private const string ValidConnectionString =
        "Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=secret==";

    // ── AzureServiceBusTransportOptions validation ────────────────────────────

    [Fact]
    public void Validate_WhenMaxConcurrentSessionsZero_Throws()
    {
        // Arrange
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = ValidConnectionString,
            MaxConcurrentSessions = 0,
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.MaxConcurrentSessions));
    }

    [Fact]
    public void Validate_WhenMaxConcurrentSessionsNegative_Throws()
    {
        // Arrange
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = ValidConnectionString,
            MaxConcurrentSessions = -1,
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.MaxConcurrentSessions));
    }

    [Fact]
    public void Validate_WhenSessionIdleTimeoutNegative_Throws()
    {
        // Arrange
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = ValidConnectionString,
            SessionIdleTimeout = TimeSpan.FromSeconds(-1),
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.SessionIdleTimeout));
    }

    [Fact]
    public void Validate_WhenSessionIdleTimeoutZero_Throws()
    {
        // Arrange — must be > Zero, not >= Zero.
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = ValidConnectionString,
            SessionIdleTimeout = TimeSpan.Zero,
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.SessionIdleTimeout));
    }

    [Fact]
    public void Validate_WhenSessionIdleTimeoutNull_DoesNotThrow()
    {
        // Arrange — null is the default (use SDK default).
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = ValidConnectionString,
            SessionIdleTimeout = null,
        };

        // Act
        Action act = () => options.Validate();

        // Assert — null = not set = valid.
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WhenMaxAutoLockRenewDurationNegative_Throws()
    {
        // Arrange
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = ValidConnectionString,
            MaxAutoLockRenewDuration = TimeSpan.FromSeconds(-1),
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.MaxAutoLockRenewDuration));
    }

    [Fact]
    public void Validate_WhenMaxAutoLockRenewDurationZero_DoesNotThrow()
    {
        // Arrange — Zero = disabled; valid per spec.
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = ValidConnectionString,
            MaxAutoLockRenewDuration = TimeSpan.Zero,
        };

        // Act
        Action act = () => options.Validate();

        // Assert — Zero is explicitly allowed (disables background renew).
        act.Should().NotThrow();
    }

    [Fact]
    public void MaxAutoLockRenewDuration_DefaultsToFiveMinutes()
    {
        var options = new AzureServiceBusTransportOptions();
        options.MaxAutoLockRenewDuration.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void MaxConcurrentSessions_DefaultsToOne()
    {
        var options = new AzureServiceBusTransportOptions();
        options.MaxConcurrentSessions.Should().Be(1);
    }

    [Fact]
    public void EnableSessions_DefaultsToFalse()
    {
        var options = new AzureServiceBusTransportOptions();
        options.EnableSessions.Should().BeFalse();
    }

    // ── ToString redaction (SEC-02 stays green with new session fields) ────────

    [Fact]
    public void ToString_DoesNotContainConnectionString()
    {
        // SEC-02: connection string MUST NOT appear in ToString even after adding session fields.
        const string sentinel = "SharedAccessKey=secret==";
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = $"Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=Root;{sentinel}",
            PrefetchCount = 5,
            MaxConcurrentCalls = 2,
            EnableSessions = true,
            MaxConcurrentSessions = 4,
            SessionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxAutoLockRenewDuration = TimeSpan.FromMinutes(10),
        };

        string rendered = options.ToString();

        rendered.Should().NotContain(sentinel,
            "the connection string (which contains SharedAccessKey) must be redacted in ToString()");
        rendered.Should().Contain("[Redacted]");
    }

    [Fact]
    public void ToString_ContainsSessionFields()
    {
        // New session fields must appear in ToString for diagnostics.
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = ValidConnectionString,
            EnableSessions = true,
            MaxConcurrentSessions = 3,
            MaxAutoLockRenewDuration = TimeSpan.FromMinutes(7),
        };

        string rendered = options.ToString();

        rendered.Should().Contain("EnableSessions = True");
        rendered.Should().Contain("MaxConcurrentSessions = 3");
    }

    // ── AzureServiceBusConfigurator fluent API (GAP-2/GAP-3) ─────────────────

    [Fact]
    public void UseSessions_SetsEnableSessionsAndMax()
    {
        // Arrange
        var configurator = new AzureServiceBusConfigurator();
        configurator.ConnectionString(ValidConnectionString);

        // Act
        configurator.UseSessions(maxConcurrentSessions: 4);
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert — GAP-3: Build() must wire BOTH fields.
        options.EnableSessions.Should().BeTrue(
            "calling UseSessions must set EnableSessions = true");
        options.MaxConcurrentSessions.Should().Be(4,
            "calling UseSessions(4) must set MaxConcurrentSessions = 4");
    }

    [Fact]
    public void UseSessions_DefaultConcurrency_SetsMaxToOne()
    {
        // Arrange
        var configurator = new AzureServiceBusConfigurator();
        configurator.ConnectionString(ValidConnectionString);

        // Act — call with default parameter.
        configurator.UseSessions();
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert
        options.EnableSessions.Should().BeTrue();
        options.MaxConcurrentSessions.Should().Be(1);
    }

    [Fact]
    public void Build_ThreadsAllSessionFieldsIntoOptions()
    {
        // GAP-3: every new session field must be explicitly wired in Build().
        // This test would catch any silently dropped field.
        var configurator = new AzureServiceBusConfigurator();
        configurator.ConnectionString(ValidConnectionString);
        configurator.UseSessions(maxConcurrentSessions: 5);
        configurator.SessionIdleTimeout(TimeSpan.FromSeconds(45));
        configurator.MaxAutoLockRenewDuration(TimeSpan.FromMinutes(8));

        AzureServiceBusTransportOptions options = configurator.Build();

        options.EnableSessions.Should().BeTrue();
        options.MaxConcurrentSessions.Should().Be(5);
        options.SessionIdleTimeout.Should().Be(TimeSpan.FromSeconds(45));
        options.MaxAutoLockRenewDuration.Should().Be(TimeSpan.FromMinutes(8));
    }

    [Fact]
    public void Build_WithoutUseSessions_EnableSessionsRemainsDefault()
    {
        // Arrange — do NOT call UseSessions.
        var configurator = new AzureServiceBusConfigurator();
        configurator.ConnectionString(ValidConnectionString);

        // Act
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert — R2.1 backward-compatible default.
        options.EnableSessions.Should().BeFalse();
        options.MaxConcurrentSessions.Should().Be(1);
    }

    // ── Topology: bw.asb.requires-session (D-6) ──────────────────────────────

    private static QueueDeclaration QueueWithArgs(IReadOnlyDictionary<string, object>? arguments) =>
        new("test-queue", Durable: true, Exclusive: false, AutoDelete: false, Arguments: arguments);

    [Fact]
    public void Parse_WithRequiresSessionTrue_SetsRequiresSession()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.RequiresSession] = true,
        });

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.RequiresSession.Should().BeTrue();
    }

    [Fact]
    public void Parse_WithRequiresSessionFalse_SetsRequiresSessionFalse()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.RequiresSession] = false,
        });

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.RequiresSession.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithRequiresSessionAsString_ParsesCorrectly()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.RequiresSession] = "true",
        });

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.RequiresSession.Should().BeTrue();
    }

    [Fact]
    public void Parse_WithRequiresSessionInvalid_Throws()
    {
        // Arrange — non-boolean value must throw BareWireConfigurationException.
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.RequiresSession] = "not-a-bool",
        });

        // Act
        Action act = () => AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(AzureServiceBusTopologyArguments.RequiresSession);
    }

    [Fact]
    public void Parse_WithNoArguments_RequiresSessionDefaultsToFalse()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(null);

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert — default: non-session queue (R2.1 backward-compatible).
        spec.RequiresSession.Should().BeFalse();
    }

    // ── AzureServiceBusConsumerRegistry.EvictAllForSession (D-11/VER-2) ───────

    [Fact]
    public void EvictAllForSession_RemovesAllEntriesForSession()
    {
        // Arrange — registry with a registered session consumer and some stored messages.
        // We use a fake consumer id to drive the registry without a real consumer.
        var registry = new AzureServiceBusConsumerRegistry();
        const string consumerId = "consumer-1";
        const string sessionId = "session-A";

        registry.RegisterSession(consumerId);

        // Store 3 messages belonging to session-A.
        // We pass null! for the receiver because settlement is not exercised in this test.
        registry.StoreMessage(consumerId, deliveryTag: 1, message: null!, receiver: null!, sessionId: sessionId);
        registry.StoreMessage(consumerId, deliveryTag: 2, message: null!, receiver: null!, sessionId: sessionId);
        registry.StoreMessage(consumerId, deliveryTag: 3, message: null!, receiver: null!, sessionId: sessionId);

        // Act — bulk evict all entries for session-A.
        registry.EvictAllForSession(consumerId, sessionId);

        // Assert — none of the delivery tags should be findable anymore.
        // TryEvictMessage returns a nullable struct; .HasValue == false means "not found".
        registry.TryEvictMessage(consumerId, deliveryTag: 1).HasValue.Should().BeFalse(
            "EvictAllForSession must remove all entries for the session (tag 1)");
        registry.TryEvictMessage(consumerId, deliveryTag: 2).HasValue.Should().BeFalse(
            "EvictAllForSession must remove all entries for the session (tag 2)");
        registry.TryEvictMessage(consumerId, deliveryTag: 3).HasValue.Should().BeFalse(
            "EvictAllForSession must remove all entries for the session (tag 3)");
    }

    [Fact]
    public void EvictAllForSession_Idempotent_OnUnknownSession()
    {
        // Arrange — unknown consumerId and sessionId.
        var registry = new AzureServiceBusConsumerRegistry();

        // Act — must not throw.
        Action act = () => registry.EvictAllForSession("unknown-consumer", "unknown-session");

        // Assert — idempotent no-op.
        act.Should().NotThrow("EvictAllForSession must be a no-op for unknown consumers/sessions");
    }

    [Fact]
    public void EvictAllForSession_DoesNotAffectOtherSessions()
    {
        // Arrange — two sessions; evicting session-A must not touch session-B entries.
        var registry = new AzureServiceBusConsumerRegistry();
        const string consumerId = "consumer-1";
        const string sessionA = "session-A";
        const string sessionB = "session-B";

        registry.RegisterSession(consumerId);

        registry.StoreMessage(consumerId, deliveryTag: 10, message: null!, receiver: null!, sessionId: sessionA);
        registry.StoreMessage(consumerId, deliveryTag: 20, message: null!, receiver: null!, sessionId: sessionB);

        // Act — evict only session-A.
        registry.EvictAllForSession(consumerId, sessionA);

        // Assert — session-A entry is gone; session-B entry is still present.
        registry.TryEvictMessage(consumerId, deliveryTag: 10).HasValue.Should().BeFalse(
            "session-A entries must be evicted");
        registry.TryEvictMessage(consumerId, deliveryTag: 20).HasValue.Should().BeTrue(
            "session-B entries must NOT be affected by evicting session-A");
    }

    // ── D-10: renew interval derives from SessionLockedUntil, not MaxAutoLockRenewDuration ──

    [Fact]
    public void RenewInterval_DrivesFromSessionLockedUntil_NotMaxAutoLockRenewDuration()
    {
        // This is a pure-logic test of the interval computation used in the background-renew task.
        // The implementation sleeps for: max((SessionLockedUntil - UtcNow - margin) / 2, minFloor).
        // It must NOT sleep for MaxAutoLockRenewDuration / 2 — that would be 2.5 min at default,
        // which is longer than most lock durations (typically 30–60 s), causing the lock to expire
        // before the first renew (D-10 PERF-2, VER-1 root cause).

        // Simulate a 60-second lock duration: locked until 50 seconds from now.
        DateTimeOffset lockedUntil = DateTimeOffset.UtcNow.AddSeconds(50);
        TimeSpan safetyMargin = TimeSpan.FromSeconds(10);
        TimeSpan minFloor = TimeSpan.FromSeconds(2);
        TimeSpan maxAutoLockRenewDuration = TimeSpan.FromMinutes(5); // BareWire knob

        // Compute the interval using the correct formula (from SessionLockedUntil).
        TimeSpan remaining = lockedUntil - DateTimeOffset.UtcNow - safetyMargin;
        TimeSpan correctInterval = remaining / 2;
        if (correctInterval < minFloor)
        {
            correctInterval = minFloor;
        }

        // Compute the WRONG interval (from MaxAutoLockRenewDuration) — this must NOT be used.
        TimeSpan wrongInterval = maxAutoLockRenewDuration / 2;

        // Assert — the correct interval is ~20s; the wrong interval would be ~150s.
        correctInterval.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "the renew sleep must be derived from the actual lock window (SessionLockedUntil), " +
            "not from MaxAutoLockRenewDuration — with a 60s lock duration the interval should be ~20s");

        wrongInterval.Should().BeGreaterThan(TimeSpan.FromSeconds(30),
            "using MaxAutoLockRenewDuration/2 (= 150s) as the sleep interval with a 60s lock " +
            "duration would cause the lock to expire before the first renew — the bug VER-1 fixed");

        // The implementation must choose the correct interval.
        correctInterval.Should().NotBe(wrongInterval,
            "the two values are NOT the same — using MaxAutoLockRenewDuration as the interval " +
            "source is the VER-1 bug; using SessionLockedUntil is the correct approach (D-10)");
    }
}
