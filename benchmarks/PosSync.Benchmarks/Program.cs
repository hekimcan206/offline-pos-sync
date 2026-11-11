using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Running;
using PosSync.Benchmarks;

// Usage (against `docker compose up -d postgres migrator seed server toxiproxy`):
//   dotnet run -c Release --project benchmarks/PosSync.Benchmarks -- payload|latency|throughput|convergence|all
//   dotnet run -c Release --project benchmarks/PosSync.Benchmarks -- micro   (BenchmarkDotNet)
// Each scenario runs one warm-up round plus RUNS measured rounds and reports the median; raw samples are written to
// benchmark-results/<scenario>.json next to the environment description.
var mode = args.FirstOrDefault() ?? "all";
if (mode == "micro")
{
    BenchmarkRunner.Run<MicroBenchmarks>(args: [.. args.Skip(1)]);
    return;
}

var config = new BenchConfig(
    ServerUrl: new Uri(Env("SERVER_URL", "http://127.0.0.1:58280/")),
    UplinkUrl: new Uri(Env("UPLINK_URL", "http://127.0.0.1:58666/")),
    ToxiproxyUrl: new Uri(Env("TOXIPROXY_URL", "http://127.0.0.1:58474/")),
    AdminKey: Env("ADMIN_KEY", "admin-dev-only"),
    ProxyUpstream: Env("PROXY_UPSTREAM", "server:8080"),
    Runs: int.Parse(Env("RUNS", "5"), CultureInfo.InvariantCulture),
    OutDir: Env("OUT_DIR", "benchmark-results"));
Directory.CreateDirectory(config.OutDir);
using var harness = new Harness(config);
var environment = Harness.Environment();
Console.WriteLine($"environment: {environment}");

var scenarios = mode == "all" ? ["payload", "latency", "throughput", "convergence"] : new[] { mode };
foreach (var scenario in scenarios)
{
    Console.WriteLine();
    Console.WriteLine($"## {scenario}");
    object result = scenario switch
    {
        "payload" => await Scenarios.PayloadAsync(),
        "latency" => await Scenarios.LocalWriteLatencyAsync(Path.Combine(Path.GetTempPath(), "possync-bench")),
        "throughput" => await Scenarios.ThroughputAsync(harness),
        "convergence" => await Scenarios.ConvergenceAsync(harness),
        _ => throw new ArgumentException($"Unknown scenario '{scenario}'."),
    };
    var file = Path.Combine(config.OutDir, $"{scenario}.json");
    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(
        new { scenario, measuredAt = DateTimeOffset.UtcNow, environment, runs = config.Runs, result },
        new JsonSerializerOptions { WriteIndented = true }));
}

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
