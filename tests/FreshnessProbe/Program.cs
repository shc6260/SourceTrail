using System.Diagnostics;
using System.Text.Json;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;

// Input validation microbenchmark, not MCP roundtrip or AI investigation time.
if (args.Length != 2) throw new ArgumentException("Usage: FreshnessProbe <absolute.sln> <report.json>");
string solution = Path.GetFullPath(args[0]);
if (!File.Exists(solution)) throw new FileNotFoundException("Solution missing", solution);
var state = new SolutionState("Partial", solution, "probe", null, [], []);
using var monitor = new SolutionInputMonitor(state, [], TimeSpan.FromSeconds(30));
long version = monitor.Version;
var baseline = await SolutionInputTracker.CaptureAsync(state, [], [], default);
monitor.CompleteScan(version);
var full = new List<double>();
var watched = new List<double>();
async Task<double> Measure(bool watch)
{
    var timer = Stopwatch.StartNew();
    if (!watch || monitor.NeedsScan)
    {
        long observed = monitor.Version;
        var next = await SolutionInputTracker.CaptureAsync(state, [], baseline, default);
        if (!SolutionInputTracker.SameContents(baseline, next)) throw new InvalidOperationException("Inputs changed during benchmark; rerun in a stable tree.");
        if (watch) monitor.CompleteScan(observed);
    }
    return timer.Elapsed.TotalMilliseconds;
}
await Measure(false); await Measure(true);
for (int i = 0; i < 5; i++)
{
    if (i % 2 == 0) { full.Add(await Measure(false)); watched.Add(await Measure(true)); }
    else { watched.Add(await Measure(true)); full.Add(await Measure(false)); }
}
static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
var report = new
{
    MeasuredAt = DateTimeOffset.UtcNow, Solution = solution, Files = baseline.Length,
    Scope = "Input validation only; source tree inputs only; build/assembly inputs excluded. Existing FileStamp hashes reused. Five alternating warm samples; no MCP or AI time.",
    StrictMs = full, WatchedMs = watched, StrictMedianMs = Median(full), WatchedMedianMs = Median(watched),
    WatcherStillNeedsScan = monitor.NeedsScan
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(args[1], json.ReplaceLineEndings("\r\n"));
Console.WriteLine(json);
