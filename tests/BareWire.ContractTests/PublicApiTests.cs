using AwesomeAssertions;
using PublicApiGenerator;
using Xunit;

using BareWire.Abstractions;
using BareWire;

namespace BareWire.ContractTests;

public sealed class PublicApiTests
{
    [Fact]
    public void Abstractions_PublicApi_ShouldMatchApproved()
    {
        var assembly = typeof(IBus).Assembly;
        var options = new ApiGeneratorOptions { IncludeAssemblyAttributes = false };
        var publicApi = assembly.GeneratePublicApi(options);

        var approvedFilePath = GetApprovedFilePath("BareWire.Abstractions");
        var approved = File.ReadAllText(approvedFilePath);

        publicApi.Should().Be(
            approved,
            because: "a breaking public API change was detected in BareWire.Abstractions — " +
                     "if intentional, update Approved/BareWire.Abstractions.approved.txt by running RegenerateAllBaselines");
    }

    [Fact]
    public void Core_PublicApi_ShouldMatchApproved()
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        var options = new ApiGeneratorOptions { IncludeAssemblyAttributes = false };
        var publicApi = assembly.GeneratePublicApi(options);

        var approvedFilePath = GetApprovedFilePath("BareWire");
        var approved = File.ReadAllText(approvedFilePath);

        publicApi.Should().Be(
            approved,
            because: "a breaking public API change was detected in BareWire — " +
                     "if intentional, update Approved/BareWire.approved.txt by running RegenerateAllBaselines");
    }

    // -------------------------------------------------------------------------
    // Single-call transport bundle packages (Feature 15 — ADR-028).
    // Each bundle exposes exactly one public AddBareWireWith{Transport} method;
    // the new registration surface lives ONLY in the bundle assemblies, never in
    // Core or a Transport assembly.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("BareWire.RabbitMQ")]
    [InlineData("BareWire.Kafka")]
    [InlineData("BareWire.AzureServiceBus")]
    [InlineData("BareWire.AWS.SQS")]
    [InlineData("BareWire.Google.PubSub")]
    public void Bundle_PublicApi_ShouldMatchApproved(string assemblyName)
    {
        var assembly = BundleAssembly(assemblyName);
        var options = new ApiGeneratorOptions { IncludeAssemblyAttributes = false };
        var publicApi = assembly.GeneratePublicApi(options);

        var approved = File.ReadAllText(GetApprovedFilePath(assemblyName));

        publicApi.Should().Be(
            approved,
            because: $"a breaking public API change was detected in {assemblyName} — " +
                     $"if intentional, update Approved/{assemblyName}.approved.txt by running RegenerateAllBaselines");
    }

    [Fact(Skip = "Manual — run to regenerate baselines")]
    public void RegenerateAllBaselines()
    {
        var options = new ApiGeneratorOptions { IncludeAssemblyAttributes = false };

        var abstractionsApi = typeof(IBus).Assembly.GeneratePublicApi(options);
        File.WriteAllText(GetApprovedFilePath("BareWire.Abstractions"), abstractionsApi);

        var coreApi = typeof(ServiceCollectionExtensions).Assembly.GeneratePublicApi(options);
        File.WriteAllText(GetApprovedFilePath("BareWire"), coreApi);

        foreach (var bundle in new[]
        {
            "BareWire.RabbitMQ",
            "BareWire.Kafka",
            "BareWire.AzureServiceBus",
            "BareWire.AWS.SQS",
            "BareWire.Google.PubSub",
        })
        {
            var bundleApi = BundleAssembly(bundle).GeneratePublicApi(options);
            File.WriteAllText(GetApprovedFilePath(bundle), bundleApi);
        }
    }

    private static System.Reflection.Assembly BundleAssembly(string assemblyName) => assemblyName switch
    {
        "BareWire.RabbitMQ" => typeof(BareWire.RabbitMQ.ServiceCollectionExtensions).Assembly,
        "BareWire.Kafka" => typeof(BareWire.Kafka.ServiceCollectionExtensions).Assembly,
        "BareWire.AzureServiceBus" => typeof(BareWire.AzureServiceBus.ServiceCollectionExtensions).Assembly,
        "BareWire.AWS.SQS" => typeof(BareWire.AWS.SQS.ServiceCollectionExtensions).Assembly,
        "BareWire.Google.PubSub" => typeof(BareWire.Google.PubSub.ServiceCollectionExtensions).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(assemblyName), assemblyName, "Unknown bundle assembly."),
    };

    private static string GetApprovedFilePath(string assemblyName)
    {
        var directory = Path.GetDirectoryName(typeof(PublicApiTests).Assembly.Location)!;
        return Path.Combine(directory, "Approved", $"{assemblyName}.approved.txt");
    }
}
