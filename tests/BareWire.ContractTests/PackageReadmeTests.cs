using AwesomeAssertions;

using Xunit;

namespace BareWire.ContractTests;

/// <summary>
/// Verifies that every packable <c>src/</c> project ships a <c>README.md</c>.
/// <para>
/// <c>Directory.Build.props</c> declares <c>&lt;PackageReadmeFile&gt;README.md&lt;/PackageReadmeFile&gt;</c>
/// and <c>&lt;None Include="README.md" Pack="true" /&gt;</c> for all <c>src/</c> projects, so a packable
/// project without a <c>README.md</c> fails <c>dotnet pack</c> with <c>NU5019: File not found</c> during
/// release — only on the CI release job, after the tag is already pushed. This test moves that failure
/// left into the regular test run so a missing readme is caught before tagging, not after.
/// </para>
/// </summary>
public sealed class PackageReadmeTests
{
    [Fact]
    public void EveryPackableSrcProject_ShouldHave_AReadme()
    {
        var srcDir = Path.Combine(FindRepositoryRoot().FullName, "src");
        Directory.Exists(srcDir).Should().BeTrue($"the src directory should exist at '{srcDir}'");

        var offenders = new List<string>();

        foreach (var projectDir in Directory.EnumerateDirectories(srcDir))
        {
            var csproj = Directory.EnumerateFiles(projectDir, "*.csproj").FirstOrDefault();
            if (csproj is null)
            {
                continue;
            }

            // Packable unless the project explicitly opts out; src/ projects inherit
            // <IsPackable>true</IsPackable> from Directory.Build.props.
            var csprojText = File.ReadAllText(csproj);
            if (csprojText.Contains("<IsPackable>false</IsPackable>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(projectDir, "README.md")))
            {
                offenders.Add(Path.GetFileName(projectDir));
            }
        }

        offenders.Should().BeEmpty(
            "every packable src/ package must ship a README.md or dotnet pack fails with NU5019; "
            + "missing in: {0}", string.Join(", ", offenders));
    }

    /// <summary>
    /// Walks up from the test output directory until the directory containing
    /// <c>BareWire.slnx</c> (the repository root) is found.
    /// </summary>
    private static DirectoryInfo FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BareWire.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the repository root containing BareWire.slnx should be locatable from the test output directory");
        return dir!;
    }
}
