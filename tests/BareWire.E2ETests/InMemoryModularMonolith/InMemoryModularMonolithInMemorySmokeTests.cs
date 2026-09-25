using System.Diagnostics;

using AwesomeAssertions;

using Xunit;

namespace BareWire.E2ETests.InMemoryModularMonolith;

/// <summary>
/// Bounded E2E smoke test for the <c>BareWire.Samples.InMemoryModularMonolith</c> sample running on
/// the in-memory transport. Launches the built sample as a subprocess in smoke mode — no RabbitMQ
/// broker and no Docker are required — and asserts that the fanout (<c>OrderPlaced</c>) and topic
/// (<c>PaymentCaptured</c>) flows reach both the Billing and Shipping modules, and that the host shuts
/// down gracefully.
/// </summary>
public sealed class InMemoryModularMonolithInMemorySmokeTests
{
    private static readonly TimeSpan SampleRunTimeout = TimeSpan.FromSeconds(90);

    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public async Task SmokeRun_InMemoryTransport_EventsFlowAcrossModulesAndHostStopsGracefully()
    {
        using var cts = new CancellationTokenSource(SampleRunTimeout);
        (int exitCode, string stdout, string stderr) = await RunSampleSmokeAsync(cts.Token);

        exitCode.Should().Be(0, $"smoke run must pass. stdout: {stdout} stderr: {stderr}");
        stdout.Should().Contain("BareWire is using the in-memory transport (environment: Development)");
        stdout.Should().Contain("Billing captured payment for order");
        stdout.Should().Contain("Shipping received OrderPlaced for order");
        stdout.Should().Contain("Shipping confirmed payment for order");
        stdout.Should().Contain("Smoke run passed: 5 orders reached ReadyToShip via InMemory");
        stdout.Should().Contain("Graceful shutdown completed");
        stdout.Should().NotContain("no inbox is registered");
    }

    /// <summary>
    /// Launches the built sample as a subprocess in smoke mode, pointed at a per-test SQLite file in
    /// the temp directory, and waits for it to exit.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunSampleSmokeAsync(
        CancellationToken cancellationToken)
    {
        string sampleDll = ResolveSampleDllPath();
        string dbPath = Path.Combine(
            Path.GetTempPath(), $"barewire-inmemory-modular-monolith-test-{Guid.NewGuid():N}.db");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // The host must load appsettings.json / appsettings.Development.json from the sample's own
            // output directory, so the working directory is the dll's directory (not the test's).
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(sampleDll);
        psi.ArgumentList.Add("--Transport=InMemory");
        psi.ArgumentList.Add("--Smoke:Enabled=true");
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__modulith"] = $"Data Source={dbPath}";

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            TryKill(process);
            process.WaitForExit(5_000);
            TryDeleteSqliteFiles(dbPath);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the check and the kill — best-effort cleanup.
        }
    }

    private static void TryDeleteSqliteFiles(string dbPath)
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            string candidate = dbPath + suffix;
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; the file may still be held briefly after process exit.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// Resolves the path to the sample's built assembly. The sample is built via a ProjectReference in
    /// this test project, so it is guaranteed to exist in the matching build configuration.
    /// </summary>
    private static string ResolveSampleDllPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BareWire.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                $"Could not locate the repository root (BareWire.slnx) from '{AppContext.BaseDirectory}'.");
        }

        string dll = Path.Combine(
            dir.FullName,
            "samples",
            "BareWire.Samples.InMemoryModularMonolith",
            "bin",
            BuildConfiguration,
            "net10.0",
            "BareWire.Samples.InMemoryModularMonolith.dll");

        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"Sample assembly not found at '{dll}'. Ensure the ProjectReference builds it.", dll);
        }

        return dll;
    }
}
