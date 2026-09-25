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
    [InlineData("BareWire.InMemory")]
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

    // -------------------------------------------------------------------------
    // Transport.InMemory is snapshotted on its own (separately from the six
    // bundle packages above): its public surface is a single entry point —
    // AddBareWireInMemory — so locking it down is cheap, and the snapshot
    // guards against an internal seam (health, drain, broker) accidentally
    // becoming public.
    // -------------------------------------------------------------------------

    [Fact]
    public void TransportInMemory_PublicApi_ShouldMatchApproved()
    {
        var assembly = typeof(BareWire.Transport.InMemory.ServiceCollectionExtensions).Assembly;
        var options = new ApiGeneratorOptions { IncludeAssemblyAttributes = false };
        var publicApi = assembly.GeneratePublicApi(options);

        var approved = File.ReadAllText(GetApprovedFilePath("BareWire.Transport.InMemory"));

        publicApi.Should().Be(
            approved,
            because: "a breaking public API change was detected in BareWire.Transport.InMemory — " +
                     "if intentional, update Approved/BareWire.Transport.InMemory.approved.txt by running RegenerateAllBaselines");
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
            "BareWire.InMemory",
        })
        {
            var bundleApi = BundleAssembly(bundle).GeneratePublicApi(options);
            File.WriteAllText(GetApprovedFilePath(bundle), bundleApi);
        }

        var transportInMemoryApi = typeof(BareWire.Transport.InMemory.ServiceCollectionExtensions).Assembly.GeneratePublicApi(options);
        File.WriteAllText(GetApprovedFilePath("BareWire.Transport.InMemory"), transportInMemoryApi);
    }

    private static System.Reflection.Assembly BundleAssembly(string assemblyName) => assemblyName switch
    {
        "BareWire.RabbitMQ" => typeof(BareWire.RabbitMQ.ServiceCollectionExtensions).Assembly,
        "BareWire.Kafka" => typeof(BareWire.Kafka.ServiceCollectionExtensions).Assembly,
        "BareWire.AzureServiceBus" => typeof(BareWire.AzureServiceBus.ServiceCollectionExtensions).Assembly,
        "BareWire.AWS.SQS" => typeof(BareWire.AWS.SQS.ServiceCollectionExtensions).Assembly,
        "BareWire.Google.PubSub" => typeof(BareWire.Google.PubSub.ServiceCollectionExtensions).Assembly,
        "BareWire.InMemory" => typeof(BareWire.InMemory.ServiceCollectionExtensions).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(assemblyName), assemblyName, "Unknown bundle assembly."),
    };

    private static string GetApprovedFilePath(string assemblyName)
    {
        var directory = Path.GetDirectoryName(typeof(PublicApiTests).Assembly.Location)!;
        return Path.Combine(directory, "Approved", $"{assemblyName}.approved.txt");
    }
}
