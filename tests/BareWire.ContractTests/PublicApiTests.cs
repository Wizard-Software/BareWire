using AwesomeAssertions;
using PublicApiGenerator;
using Xunit;

using BareWire.Abstractions;
using BareWire.Core;

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

        var approvedFilePath = GetApprovedFilePath("BareWire.Core");
        var approved = File.ReadAllText(approvedFilePath);

        publicApi.Should().Be(
            approved,
            because: "a breaking public API change was detected in BareWire.Core — " +
                     "if intentional, update Approved/BareWire.Core.approved.txt by running RegenerateAllBaselines");
    }

    [Fact(Skip = "Manual — run to regenerate baselines")]
    public void RegenerateAllBaselines()
    {
        var options = new ApiGeneratorOptions { IncludeAssemblyAttributes = false };

        var abstractionsApi = typeof(IBus).Assembly.GeneratePublicApi(options);
        File.WriteAllText(GetApprovedFilePath("BareWire.Abstractions"), abstractionsApi);

        var coreApi = typeof(ServiceCollectionExtensions).Assembly.GeneratePublicApi(options);
        File.WriteAllText(GetApprovedFilePath("BareWire.Core"), coreApi);
    }

    private static string GetApprovedFilePath(string assemblyName)
    {
        var directory = Path.GetDirectoryName(typeof(PublicApiTests).Assembly.Location)!;
        return Path.Combine(directory, "Approved", $"{assemblyName}.approved.txt");
    }
}
