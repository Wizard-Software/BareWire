using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.Kafka.Configuration;
using BareWire.Transport.Kafka.Internal;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaRetryDlqConfiguratorTests
{
    [Fact]
    public void Build_NoEnable_ReturnsDisabledWithDefaults()
    {
        // Arrange
        var configurator = new KafkaRetryDlqConfigurator();

        // Act
        KafkaRetryDlqOptions options = configurator.Build();

        // Assert — opt-in default: disabled, defaults preserved
        options.Enabled.Should().BeFalse();
        options.MaxRetryCount.Should().Be(3);
        options.RetryTopicSuffix.Should().Be(".retry");
        options.DlqTopicSuffix.Should().Be(".DLQ");
    }

    [Fact]
    public void Build_Enable_SetsEnabledTrue()
    {
        // Arrange
        var configurator = new KafkaRetryDlqConfigurator();
        configurator.Enable();

        // Act
        KafkaRetryDlqOptions options = configurator.Build();

        // Assert
        options.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Build_FluentSettings_PropagatedToOptions()
    {
        // Arrange
        var configurator = new KafkaRetryDlqConfigurator();
        configurator.Enable();
        configurator.MaxRetries(5);
        configurator.RetryTopicSuffix("-retry");
        configurator.DlqTopicSuffix("-dead");
        configurator.Backoff(TimeSpan.FromSeconds(2), multiplier: 3.0, TimeSpan.FromMinutes(10));

        // Act
        KafkaRetryDlqOptions options = configurator.Build();

        // Assert
        options.MaxRetryCount.Should().Be(5);
        options.RetryTopicSuffix.Should().Be("-retry");
        options.DlqTopicSuffix.Should().Be("-dead");
        options.BaseDelay.Should().Be(TimeSpan.FromSeconds(2));
        options.BackoffMultiplier.Should().Be(3.0);
        options.MaxDelay.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Build_EnabledWithNegativeMaxRetries_ThrowsConfigurationException()
    {
        // Arrange — validation runs only when enabled
        var configurator = new KafkaRetryDlqConfigurator();
        configurator.Enable();
        configurator.MaxRetries(-1);

        // Act
        Action act = () => configurator.Build();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.MaxRetryCount));
    }

    [Fact]
    public void Build_EnabledWithInvalidSuffixChars_ThrowsConfigurationException()
    {
        // Arrange — suffix with a space is not a valid Kafka topic character
        var configurator = new KafkaRetryDlqConfigurator();
        configurator.Enable();
        configurator.RetryTopicSuffix(".bad suffix");

        // Act
        Action act = () => configurator.Build();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.RetryTopicSuffix));
    }

    [Fact]
    public void Build_EnabledWithMultiplierBelowOne_ThrowsConfigurationException()
    {
        // Arrange
        var configurator = new KafkaRetryDlqConfigurator();
        configurator.Enable();
        configurator.Backoff(TimeSpan.FromSeconds(1), multiplier: 0.5, TimeSpan.FromMinutes(1));

        // Act
        Action act = () => configurator.Build();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.BackoffMultiplier));
    }

    [Fact]
    public void Build_EnabledWithMaxDelayBelowBaseDelay_ThrowsConfigurationException()
    {
        // Arrange
        var configurator = new KafkaRetryDlqConfigurator();
        configurator.Enable();
        configurator.Backoff(TimeSpan.FromMinutes(5), multiplier: 2.0, TimeSpan.FromSeconds(1));

        // Act
        Action act = () => configurator.Build();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.MaxDelay));
    }

    [Fact]
    public void Build_DisabledWithInvalidValues_DoesNotThrow()
    {
        // Arrange — a disabled instance is never used for routing, so validation is skipped
        var configurator = new KafkaRetryDlqConfigurator();
        configurator.MaxRetries(-1);

        // Act
        Action act = () => configurator.Build();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RetryTopicSuffix_Null_ThrowsArgumentException()
    {
        var configurator = new KafkaRetryDlqConfigurator();
        Action act = () => configurator.RetryTopicSuffix(null!);
        act.Should().Throw<ArgumentException>();
    }
}
