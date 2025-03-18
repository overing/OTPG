using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Diagnostics.Runtime;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Scalar.AspNetCore;

if (args.Contains("--heap-capture"))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://+:10255");
    Environment.SetEnvironmentVariable("ASPNETCORE_ApplicationName", Assembly.GetExecutingAssembly().GetName().Name + "-hc");

    var hcBuilder = WebApplication.CreateBuilder(args);
    hcBuilder.Logging.ClearProviders().AddSimpleConsole();
    hcBuilder.Services
        .AddHostedService<HeapInfoCapture>()
        .AddSingleton<IAppMetrics, AppMetrics>()
        .AddOpenTelemetry()
        .WithMetrics(metrics => metrics
            .ConfigureResource(resource => resource.AddService(serviceName: hcBuilder.Environment.ApplicationName))
            .AddMeter(hcBuilder.Environment.ApplicationName)
            .AddPrometheusExporter());

    var hc = hcBuilder.Build();
    hc.MapPrometheusScrapingEndpoint().RequireHost("*:10255");
    await hc.RunAsync();
    return;
}

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Logging
    .ClearProviders()
    .AddDebug()
    .AddSimpleConsole();

appBuilder.Services.AddOpenApi();

#region 建立 Meter 與 Counter 加入 OpenTelemetry 與 Prometheus Exporter

appBuilder.Services
    .AddSingleton<IAppMetrics, AppMetrics>()
    .AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .ConfigureResource(resource => resource.AddService(serviceName: appBuilder.Environment.ApplicationName))
        .AddMeter(appBuilder.Environment.ApplicationName)
        .AddPrometheusExporter());

#endregion

appBuilder.Services.AddHostedService<CaptureProcessHolder>();

appBuilder.Services
    .AddHostedService(p => p.GetRequiredService<FakeAccess>())
    .AddHttpClient<FakeAccess>(client => client.BaseAddress = new("http://localhost:10254/"));

var app = appBuilder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

#region 註冊給 Prometheus 撈取資料的端點並限制 port

app.MapPrometheusScrapingEndpoint().RequireHost("*:10254");

#endregion

const string ActionPath = "/action";
app.MapGet(ActionPath, async ([FromServices] IAppMetrics metrics) =>
{
    var sw = ValueStopwatch.StartNew();
    var result = Results.Ok("Is work !! :D");

    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 300)));

    #region 在需要的位置對 Counter 進行操作

    metrics.IncrementRequestCount(ActionPath);
    metrics.LogRequestHandleTime(ActionPath, (long)sw.GetElapsedTime().TotalMilliseconds);

    #endregion

    return result;
});

await app.RunAsync();

internal sealed class AppMetrics : IAppMetrics
{
    private readonly Counter<long> _request;
    private readonly Gauge<long> _elapsedMs;
    private readonly Gauge<long> _objectCount;
    private readonly Gauge<double> _objectSize;
    private readonly Gauge<long> _captureMs;
    private readonly Counter<long> _capture;
    private readonly Counter<long> _captureRestart;

    public AppMetrics(IHostEnvironment hostEnvironment, IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(name: hostEnvironment.ApplicationName, version: "1.0.0");
        _request = meter.CreateCounter<long>(name: "request.count", description: "Counts the number of request");
        _elapsedMs = meter.CreateGauge<long>(name: "request.elapsed", unit: "ms");
        _objectCount = meter.CreateGauge<long>(name: "obj.count");
        _objectSize = meter.CreateGauge<double>(name: "obj.size", unit: "bytes");
        _captureMs = meter.CreateGauge<long>(name: "capture.elapsed", unit: "ms");
        _capture = meter.CreateCounter<long>(name: "capture.count");
        _captureRestart = meter.CreateCounter<long>(name: "capture.restart");
    }

    public void IncrementRequestCount(string name) => _request.Add(1, [new("action", name)]);

    public void LogRequestHandleTime(string name, long ms) => _elapsedMs.Record(ms, [new("action", name)]);

    public void LogHeapObject(ClrType type, uint count, ulong size)
    {
        var tags = new KeyValuePair<string, object?>[2]
        {
            new("type", type.Name),
            new("assembly", type.Module.AssemblyName),
        };
        _objectCount.Record(count, tags);
        _objectSize.Record(size, tags);
    }

    public void LogCaptureTime(long ms) => _captureMs.Record(ms);

    public void IncrementCaptureCount() => _capture.Add(1);

    public void IncrementCaptureRestartCount() => _captureRestart.Add(1);
}

internal interface IAppMetrics
{
    void IncrementRequestCount(string name);
    void LogRequestHandleTime(string name, long ms);
    void LogHeapObject(ClrType type, uint count, ulong size);
    void LogCaptureTime(long ms);
    void IncrementCaptureCount();
    void IncrementCaptureRestartCount();
}

internal sealed class FakeAccess(HttpClient client) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(3000, 12000)), stoppingToken);

            _ = client.GetAsync("action", stoppingToken);
        }
    }
}

internal sealed class CaptureProcessHolder(IAppMetrics appMetrics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startInfo = new ProcessStartInfo("DockerAppDemo", ["--heap-capture"]);
        while (!stoppingToken.IsCancellationRequested)
        {
            using var subprocess = Process.Start(startInfo)!;
            await subprocess.WaitForExitAsync(stoppingToken);

            if (subprocess.ExitCode == 0)
            {
                break;
            }

            appMetrics.IncrementCaptureRestartCount();
        }
    }
}

internal sealed class HeapInfoCapture(IHostEnvironment hostEnvironment, IAppMetrics appMetrics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var executorPath = await EnsureDumpExecutorAsync(stoppingToken);

        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        var cd = hostEnvironment.ContentRootPath;
        var dumpfile = Path.Combine(cd, "HeapInfoCapture.dmp");
        var startInfo = new ProcessStartInfo(executorPath, ["collect", "-o", dumpfile, "-p", "1"]);
        while (!stoppingToken.IsCancellationRequested)
        {
            var vsw = ValueStopwatch.StartNew();

            using (var collect = Process.Start(startInfo)!)
            {
                await collect.WaitForExitAsync(stoppingToken);
            }

            using (var target = DataTarget.LoadDump(dumpfile))
            using (var runtime = target.ClrVersions[0].CreateRuntime())
            {
                var groups = runtime.Heap.EnumerateObjects()
                    .Where(o => o.Type?.Name is { } name && IsTargetName(name))
                    .GroupBy(o => o.Type!);
                foreach (var group in groups)
                {
                    ulong totalSize = 0;
                    uint count = 0;
                    foreach (var obj in group)
                    {
                        totalSize += obj.Size;
                        ++count;
                    }
                    appMetrics.LogHeapObject(group.Key, count, totalSize);
                }
            }

            File.Delete(dumpfile);

            var elapsed = (long)vsw.GetElapsedTime().TotalMilliseconds;
            appMetrics.LogCaptureTime(elapsed);
            appMetrics.IncrementCaptureCount();

            await Task.Delay(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(elapsed), stoppingToken);
        }
    }

    private static readonly string[] IgnorePrefix = [
        "<>",

        "Internal.",
        "Interop+",

        "OpenTelemetry.",

        "Scalar.AspNetCore.",
    ];

    private static bool IsTargetName(string name)
    {
        foreach (var prefix in IgnorePrefix)
        {
            if (name.AsSpan().StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<string> EnsureDumpExecutorAsync(CancellationToken cancellationToken)
    {
        var executor = Path.Combine(hostEnvironment.ContentRootPath, "dotnet-dump");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            executor += ".exe";
        }

        if (!File.Exists(executor))
        {
            var url = $"https://aka.ms/dotnet-dump/{RuntimeInformation.RuntimeIdentifier}";
            using (var httpClient = new HttpClient())
            using (var download = await httpClient.GetStreamAsync(url, cancellationToken))
            using (var loaclfile = File.OpenWrite(executor))
            {
                await download.CopyToAsync(loaclfile, cancellationToken);
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var chmod = Process.Start(startInfo: new("chmod", ["+x", executor]) { UseShellExecute = true })!;
                await chmod.WaitForExitAsync(cancellationToken);
            }
        }

        return executor;
    }
}

internal readonly struct ValueStopwatch
{
    private readonly long _startTimestamp;

    public bool IsActive => _startTimestamp != 0;

    private ValueStopwatch(long startTimestamp) => _startTimestamp = startTimestamp;

    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

    public TimeSpan GetElapsedTime()
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("An uninitialized, or 'default', ValueStopwatch cannot be used to get elapsed time.");
        }

        return Stopwatch.GetElapsedTime(_startTimestamp, Stopwatch.GetTimestamp());
    }
}
