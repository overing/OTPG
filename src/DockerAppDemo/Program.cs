using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Diagnostics.Runtime;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Scalar.AspNetCore;

if (CaptureProcessHolder.TryBuildHeapCaptureApp(out var hcApp))
{
    await hcApp.RunAsync();
    return;
}

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Logging
    .ClearProviders()
    .AddDebug()
    .AddSimpleConsole();

appBuilder.Services.AddOpenApi();

// 建立 Meter 與 Counter 加入 OpenTelemetry 與 Prometheus Exporter
appBuilder.Services
    .AddSingleton<IAppMetrics, AppMetrics>()
    .AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .ConfigureResource(resource => resource.AddService(serviceName: appBuilder.Environment.ApplicationName))
        .AddMeter(appBuilder.Environment.ApplicationName)
        .AddPrometheusExporter());

appBuilder.Services
    .AddHostedService(p => p.GetRequiredService<CaptureProcessHolder>())
    .AddSingleton<CaptureProcessHolder>();

appBuilder.Services
    .AddHttpClient(nameof(FakeAccess), client => client.BaseAddress = new("http://localhost:7071/")).Services
    .AddHostedService(p => p.GetRequiredService<FakeAccess>())
    .AddSingleton<FakeAccess>();

var app = appBuilder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

// 註冊給 Prometheus 撈取資料的端點並限制 port
app.MapPrometheusScrapingEndpoint().RequireHost("*:10254");

const string ActionPath = "/action";
app.MapGet(ActionPath, async ([FromServices] IAppMetrics metrics) =>
{
    var sw = ValueStopwatch.StartNew();
    var result = Results.Ok("Is work !! :D");

    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 300)));

    metrics.IncrementRequestCount(ActionPath);
    metrics.LogRequestHandleTime(ActionPath, (long)sw.GetElapsedTime().TotalMilliseconds);

    return result;
});

app.MapPost("/toggle_fake_access", ([FromServices] FakeAccess access, [FromBody] long toggle) =>
{
    if (toggle == 0)
    {
        access.Pause();
    }
    else
    {
        access.Resume();
    }
});

app.MapPost("/toggle_collect", ([FromServices] CaptureProcessHolder holder, [FromBody] long toggle) =>
{
    if (toggle == 0)
    {
        holder.Pause();
    }
    else
    {
        holder.Resume();
    }
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

    public AppMetrics(IConfiguration configuration, IMeterFactory meterFactory)
    {
        var name = configuration.GetValue<string>("METER_NAME") ?? throw new ArgumentException("'METER_NAME' config is required");
        var meter = meterFactory.Create(name, version: "1.0.0");

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

internal sealed class FakeAccess(IHttpClientFactory clientFactory) : BackgroundService
{
    private long _pause;

    private readonly HttpClient _client = clientFactory.CreateClient(nameof(FakeAccess));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (Interlocked.Read(ref _pause) == 0)
            {
                try
                {
                    _ = _client.GetAsync("action", stoppingToken);
                }
                catch (TaskCanceledException)
                {
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(3000, 12000)), stoppingToken);
        }
    }

    public void Pause() => Interlocked.Exchange(ref _pause, 1);

    public void Resume() => Interlocked.Exchange(ref _pause, 0);
}

/// <summary>
/// 因為反覆的在 docker 環境中執行 dotnet-dump collect 有可能會導致 process 崩潰
/// 所以需要透過 subprocess 去執行再將指標匯出到獨立的 prometheus port
/// </summary>
internal sealed class CaptureProcessHolder(IAppMetrics appMetrics) : BackgroundService
{
    private long _pause;
    private Process? _currentProcess;

    private const string KeyArg = "--heap-capture";

    public static bool TryBuildHeapCaptureApp([NotNullWhen(returnValue: true)] out WebApplication? heapCaptureApp)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Contains(KeyArg))
        {
            var appBuilder = WebApplication.CreateBuilder(args);
            var meterName = appBuilder.Configuration.GetValue<string>("METER_NAME")
                ?? throw new ArgumentException("'METER_NAME' config is required");
            appBuilder.Logging.ClearProviders().AddSimpleConsole();
            appBuilder.Services
                .AddHostedService<HeapInfoCapture>()
                .AddSingleton<IAppMetrics, AppMetrics>()
                .AddOpenTelemetry()
                .WithMetrics(metrics => metrics
                    .ConfigureResource(resource => resource.AddService(serviceName: meterName))
                    .AddMeter(meterName)
                    .AddPrometheusExporter());

            var app = appBuilder.Build();
            app.MapPrometheusScrapingEndpoint().RequireHost("*:10255");
            heapCaptureApp = app;
            return true;
        }
        heapCaptureApp = null;
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startInfo = new ProcessStartInfo("DockerAppDemo", [KeyArg]);
        startInfo.Environment["ASPNETCORE_URLS"] = "http://+:10255";
        startInfo.Environment["ASPNETCORE_METER_NAME"] = Assembly.GetExecutingAssembly().GetName().Name + "-hc";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (Interlocked.Read(ref _pause) > 0)
                {
                    await Task.Delay(33, stoppingToken);
                }

                var process = Process.Start(startInfo)!;

                _currentProcess = process;

                await process.WaitForExitAsync(stoppingToken);

                if (process.ExitCode == 0)
                {
                    break;
                }
            }
            finally
            {
                _currentProcess = null;
            }

            appMetrics.IncrementCaptureRestartCount();
        }
    }

    public void Pause()
    {
        if (Interlocked.CompareExchange(ref _pause, value: 1, comparand: 0) == 0)
        {
            if (_currentProcess is { } process)
            {
                process.Kill();
                process.Dispose();
                _currentProcess = null;
            }
        }
    }

    public void Resume() => Interlocked.Exchange(ref _pause, 0);
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
            if (name.AsSpan().StartsWith(prefix))
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
