using AwesomeAssertions;
using BareWire.Abstractions.Saga;
using BareWire.Saga.RoutingSlip;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.UnitTests.Saga;

// ── Test value types ──────────────────────────────────────────────────────────

internal sealed record TestArgs(string Value);
internal sealed record TestLog(string Value);

// ── Test activity doubles ─────────────────────────────────────────────────────

/// <summary>Succeeds on execute; no-ops on compensate.</summary>
internal sealed class SuccessActivity : ICompensableActivity<TestArgs, TestLog>
{
    public Task<TestLog> ExecuteAsync(TestArgs args, CancellationToken cancellationToken = default)
        => Task.FromResult(new TestLog(args.Value));

    public Task CompensateAsync(TestLog log, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>Succeeds on execute; records compensation calls in order.</summary>
internal sealed class TrackingActivity : ICompensableActivity<TestArgs, TestLog>
{
    private readonly List<string> _executionLog;

    internal TrackingActivity(List<string> executionLog)
    {
        _executionLog = executionLog;
    }

    public Task<TestLog> ExecuteAsync(TestArgs args, CancellationToken cancellationToken = default)
    {
        _executionLog.Add($"execute:{args.Value}");
        return Task.FromResult(new TestLog(args.Value));
    }

    public Task CompensateAsync(TestLog log, CancellationToken cancellationToken = default)
    {
        _executionLog.Add($"compensate:{log.Value}");
        return Task.CompletedTask;
    }
}

/// <summary>Always throws on execute; no-ops on compensate.</summary>
internal sealed class FailingActivity : ICompensableActivity<TestArgs, TestLog>
{
    public Task<TestLog> ExecuteAsync(TestArgs args, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Activity failed");

    public Task CompensateAsync(TestLog log, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>Succeeds on execute; always throws on compensate.</summary>
internal sealed class FailingCompensationActivity : ICompensableActivity<TestArgs, TestLog>
{
    public Task<TestLog> ExecuteAsync(TestArgs args, CancellationToken cancellationToken = default)
        => Task.FromResult(new TestLog(args.Value));

    public Task CompensateAsync(TestLog log, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Compensation failed");
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class RoutingSlipExecutorTests
{
    private static RoutingSlipExecutor CreateExecutor()
        => new(NullLogger<RoutingSlipExecutor>.Instance);

    [Fact]
    public async Task ExecuteAsync_AllActivitiesSucceed_ReturnsCompleted()
    {
        // Arrange
        var executor = CreateExecutor();
        var slots = new RoutingSlipBuilder()
            .AddActivity(new SuccessActivity(), new TestArgs("A"))
            .AddActivity(new SuccessActivity(), new TestArgs("B"))
            .AddActivity(new SuccessActivity(), new TestArgs("C"))
            .Build();

        // Act
        var result = await executor.ExecuteAsync(slots, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RoutingSlipCompleted>();
        var completed = (RoutingSlipCompleted)result;
        completed.Logs.Should().HaveCount(3);
        completed.Logs[0].Should().BeOfType<TestLog>().Which.Value.Should().Be("A");
        completed.Logs[1].Should().BeOfType<TestLog>().Which.Value.Should().Be("B");
        completed.Logs[2].Should().BeOfType<TestLog>().Which.Value.Should().Be("C");
    }

    [Fact]
    public async Task ExecuteAsync_SecondActivityFails_CompensatesFirstInReverse()
    {
        // Arrange
        var executor = CreateExecutor();
        var callLog = new List<string>();

        var slots = new RoutingSlipBuilder()
            .AddActivity(new TrackingActivity(callLog), new TestArgs("step-0"))
            .AddActivity(new FailingActivity(), new TestArgs("step-1"))
            .Build();

        // Act
        var result = await executor.ExecuteAsync(slots, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RoutingSlipFaulted>();
        var faulted = (RoutingSlipFaulted)result;
        faulted.Exception.Should().BeOfType<InvalidOperationException>();
        faulted.FailedAtStep.Should().Be(1);
        faulted.CompensationSucceeded.Should().BeTrue();

        // step-0 executed, then step-1 failed, so step-0 must be compensated.
        callLog.Should().ContainInOrder("execute:step-0", "compensate:step-0");
    }

    [Fact]
    public async Task ExecuteAsync_CompensationAlsoFails_LogsAndContinues()
    {
        // Arrange
        var executor = CreateExecutor();

        // step-0: succeeds on execute, fails on compensate
        // step-1: fails on execute → triggers compensation of step-0
        var slots = new RoutingSlipBuilder()
            .AddActivity(new FailingCompensationActivity(), new TestArgs("step-0"))
            .AddActivity(new FailingActivity(), new TestArgs("step-1"))
            .Build();

        // Act
        var result = await executor.ExecuteAsync(slots, CancellationToken.None);

        // Assert: still returns Faulted (with the original execute exception),
        //         but compensationSucceeded is false because step-0's compensation threw.
        result.Should().BeOfType<RoutingSlipFaulted>();
        var faulted = (RoutingSlipFaulted)result;
        faulted.FailedAtStep.Should().Be(1);
        faulted.CompensationSucceeded.Should().BeFalse();
        faulted.Exception.Message.Should().Be("Activity failed");
    }

    [Fact]
    public async Task ExecuteAsync_EmptySlip_ReturnsCompleted()
    {
        // Arrange
        var executor = CreateExecutor();
        var slots = new RoutingSlipBuilder().Build();

        // Act
        var result = await executor.ExecuteAsync(slots, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RoutingSlipCompleted>();
        var completed = (RoutingSlipCompleted)result;
        completed.Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SingleActivity_ExecutesAndReturnsLog()
    {
        // Arrange
        var executor = CreateExecutor();
        var slots = new RoutingSlipBuilder()
            .AddActivity(new SuccessActivity(), new TestArgs("only-step"))
            .Build();

        // Act
        var result = await executor.ExecuteAsync(slots, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RoutingSlipCompleted>();
        var completed = (RoutingSlipCompleted)result;
        completed.Logs.Should().HaveCount(1);
        completed.Logs[0].Should().BeOfType<TestLog>().Which.Value.Should().Be("only-step");
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_PropagatesCancellation()
    {
        // Arrange
        var executor = CreateExecutor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var slots = new RoutingSlipBuilder()
            .AddActivity(new SuccessActivity(), new TestArgs("step"))
            .Build();

        // Act & Assert
        Func<Task> act = () => executor.ExecuteAsync(slots, cts.Token);
        await act.Should().ThrowExactlyAsync<OperationCanceledException>();
    }
}
