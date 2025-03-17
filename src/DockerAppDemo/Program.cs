using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    Environment.SetEnvironmentVariable("ASPNETCORE_ApplicationName", "DockerAppDemo-hc");

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

appBuilder.Services
    .AddHostedService(p => p.GetRequiredService<FakeAccess>())
    .AddHttpClient<FakeAccess>(client => client.BaseAddress = new("http://localhost:10254/"));

var app = appBuilder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

#region 註冊給 Prometheus 撈取資料的端點並限制 port

app.MapPrometheusScrapingEndpoint().RequireHost("*:10254");

#endregion

app.MapGet("/action", async ([FromServices] IAppMetrics metrics) =>
{
    var sw = Stopwatch.StartNew();
    var result = Results.Ok("Is work !! :D");

    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 300)));

    #region 在需要的位置對 Counter 進行操作

    metrics.IncrementRequestCount("/action");
    metrics.LogRequestHandleTime("/action", sw.ElapsedMilliseconds);

    #endregion

    return result;
});

app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
{
    var pid = Environment.ProcessId;
    app.Services.GetRequiredService<ILogger<Program>>().LogInformation("Main Process Id: {pid}", pid);
    Process.Start(startInfo: new("DockerAppDemo", ["--heap-capture"]));
});

await app.RunAsync();

sealed class AppMetrics : IAppMetrics
{
    private readonly Counter<long> _request;
    private readonly Gauge<long> _elapsedMs;
    private readonly Gauge<long> _objectCount;
    private readonly Gauge<double> _objectSize;

    public AppMetrics(IHostEnvironment hostEnvironment, IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(name: hostEnvironment.ApplicationName, version: "1.0.0");
        _request = meter.CreateCounter<long>(name: "request.count", description: "Counts the number of request");
        _elapsedMs = meter.CreateGauge<long>(name: "request.elapsed_time", unit: "ms");
        _objectCount = meter.CreateGauge<long>(name: "obj.count");
        _objectSize = meter.CreateGauge<double>(name: "obj.size", unit: "byte");
    }

    public void IncrementRequestCount(string name) => _request.Add(1, [new("action", name)]);

    public void LogRequestHandleTime(string name, long ms) => _elapsedMs.Record(ms, [new("action", name)]);

    public void LogHeapObject(string name, uint count, ulong size)
    {
        var tags = new[] { KeyValuePair.Create("name", (object?)name) };
        _objectCount.Record(count, tags);
        _objectSize.Record(size, tags);
    }
}

interface IAppMetrics
{
    void IncrementRequestCount(string name);
    void LogRequestHandleTime(string name, long ms);
    void LogHeapObject(string name, uint count, ulong size);
}

sealed class FakeAccess(HttpClient client) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            _ = client.GetAsync("action", stoppingToken);
        }
    }
}

sealed class HeapInfoCapture(ILogger<HeapInfoCapture> logger, IHostEnvironment hostEnvironment, IAppMetrics appMetrics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(nameof(ExecuteAsync));

        var cd = hostEnvironment.ContentRootPath;
        var executor = Path.Combine(cd, "dotnet-dump");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            executor += ".exe";
        }
        if (!File.Exists(executor))
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            var url = $"https://aka.ms/dotnet-dump/{rid}";
            using var httpClient = new HttpClient();
            using var download = await httpClient.GetStreamAsync(url, stoppingToken);
            using var loaclfile = File.OpenWrite(executor);
            await download.CopyToAsync(loaclfile, stoppingToken);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var chmod = Process.Start(startInfo: new("chmod", ["+x", executor]) { UseShellExecute = true })!;
                await chmod.WaitForExitAsync(stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            // logger.LogInformation("Capture heap");

            var dumpname = $"{DateTime.Now:yyyyMMdd-HHmmss}.dmp";
            var dumpfile = Path.Combine(cd, dumpname);

            using var collect = Process.Start(startInfo: new(executor, ["collect", "-o", dumpfile, "-p", "1"]))!;
            await collect.WaitForExitAsync(stoppingToken);

            var datas = CaptureObjectCountAndSize(dumpfile);
            logger.LogInformation("Capture heap: x{count}", datas.Count);

            File.Delete(dumpfile);

            foreach (var data in datas)
            {
                appMetrics.LogHeapObject(data.Key, data.Value.Count, data.Value.Size);
            }
        }
    }

    static Dictionary<string, (ulong Size, uint Count)> CaptureObjectCountAndSize(string dumpFile)
    {
        using var target = DataTarget.LoadDump(dumpFile);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        return runtime.Heap.EnumerateObjects()
            .Where(o => o.Type?.Name is { } name && IsTargetName(name))
            .GroupBy(o => o.Type!.Name!)
            .Select(Summary)
            .ToDictionary(i => i.Type, i => (i.Size, i.Count));

        static (string Type, ulong Size, uint Count) Summary(IGrouping<string, ClrObject> objGroup)
        {
            ulong totalSize = 0;
            uint count = 0;
            foreach (var obj in objGroup)
            {
                totalSize += obj.Size;
                ++count;
            }
            return (Type: objGroup.Key, Size: totalSize, Count: count);
        }
    }

    static readonly string[] IgnorePrefix = [
        "<>",
        "Internal.",
        "Interop+",

        "Microsoft.AspNetCore.",
        "Microsoft.Diagnostics.",
        "Microsoft.Extensions.",
        "Microsoft.Win32.",

        "OpenTelemetry.",

        "System.Action<",
        "System.Buffers.",
        "System.Collections.",
        "System.Comparison<",
        "System.Diagnostics.",
        "System.Dynamic.",
        "System.Formats.",
        "System.Func<",
        "System.Globalization.",
        "System.IO.FileSystemWatcher+",
        "System.IO.Pipelines.",
        "System.Lazy<",
        "System.Linq.",
        "System.Net.",
        "System.Reflection.",
        "System.Resources.",
        "System.Runtime.",
        "System.RuntimeType",
        "System.Security.",
        "System.SZ",
        "System.Text.",
        "System.Threading.",
        "System.TimeZoneInfo.",
        "System.WeakReference<",
    ];

    static bool IsTargetName(string name)
    {
        foreach (var prefix in IgnorePrefix)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
