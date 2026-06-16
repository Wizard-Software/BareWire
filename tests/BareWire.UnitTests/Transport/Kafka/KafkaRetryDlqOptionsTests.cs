using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.Kafka.Internal;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaRetryDlqOptionsTests
{
    [Fact]
    public void Defaults_AreOptInDisabledWithSaneValues()
    {
        // Arrange / Act
        var options = new KafkaRetryDlqOptions();

        // Assert
        options.Enabled.Should().BeFalse();
        options.MaxRetryCount.Should().Be(3);
        options.RetryTopicSuffix.Should().Be(".retry");
        options.DlqTopicSuffix.Should().Be(".DLQ");
        options.BaseDelay.Should().Be(TimeSpan.FromSeconds(1));
        options.BackoffMultiplier.Should().Be(2.0);
        options.MaxDelay.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Validate_Defaults_DoesNotThrow()
    {
        var options = new KafkaRetryDlqOptions();
        Action act = options.Validate;
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_NegativeMaxRetryCount_Throws()
    {
        var options = new KafkaRetryDlqOptions { MaxRetryCount = -1 };
        Action act = options.Validate;
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.MaxRetryCount));
    }

    [Theory]
    [InlineData(".bad suffix")]   // space
    [InlineData(".bad/suffix")]   // slash
    [InlineData(".bad:suffix")]   // colon
    public void Validate_InvalidSuffixChars_Throws(string suffix)
    {
        var options = new KafkaRetryDlqOptions { RetryTopicSuffix = suffix };
        Action act = options.Validate;
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.RetryTopicSuffix));
    }

    [Fact]
    public void Validate_EmptyDlqSuffix_Throws()
    {
        var options = new KafkaRetryDlqOptions { DlqTopicSuffix = string.Empty };
        Action act = options.Validate;
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.DlqTopicSuffix));
    }

    [Fact]
    public void Validate_MultiplierBelowOne_Throws()
    {
        var options = new KafkaRetryDlqOptions { BackoffMultiplier = 0.9 };
        Action act = options.Validate;
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.BackoffMultiplier));
    }

    [Fact]
    public void Validate_NonPositiveBaseDelay_Throws()
    {
        var options = new KafkaRetryDlqOptions { BaseDelay = TimeSpan.Zero };
        Action act = options.Validate;
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.BaseDelay));
    }

    [Fact]
    public void Validate_MaxDelayBelowBaseDelay_Throws()
    {
        var options = new KafkaRetryDlqOptions
        {
            BaseDelay = TimeSpan.FromMinutes(2),
            MaxDelay = TimeSpan.FromSeconds(1),
        };
        Action act = options.Validate;
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaRetryDlqOptions.MaxDelay));
    }
}
