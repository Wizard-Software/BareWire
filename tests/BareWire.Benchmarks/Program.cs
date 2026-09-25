using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

// Fails the process loudly when any benchmark run reports a critical validation error or an
// unsuccessful report, instead of always exiting 0 regardless of outcome. Informational invocations
// (`--list`, `--info`, `--help`, `--version`) produce no summaries and still exit 0; any other
// invocation that produces no summary (for example a filter that matches nothing) exits non-zero.
Summary[] summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args).ToArray();

string[] informationalSwitches = ["--list", "--info", "--help", "-h", "-?", "--version"];
bool isInformational = args.Any(a => informationalSwitches.Contains(a, StringComparer.OrdinalIgnoreCase));
bool hasFailure = summaries.Length == 0 && !isInformational;
foreach (Summary summary in summaries)
{
    if (summary.HasCriticalValidationErrors)
    {
        hasFailure = true;
        continue;
    }

    foreach (BenchmarkReport report in summary.Reports)
    {
        if (!report.Success)
        {
            hasFailure = true;
        }
    }
}

return hasFailure ? 1 : 0;
