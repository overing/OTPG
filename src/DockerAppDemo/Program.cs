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

appBuilder.Services.AddSingleton<IList<Order>, List<Order>>();

var app = appBuilder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

// 註冊給 Prometheus 撈取資料的端點並限制 port
app.MapPrometheusScrapingEndpoint().RequireHost("*:10254");

app.MapGet("/", () => Results.Redirect("/scalar/", permanent: true));

app.MapPost("/add_order", ([FromServices] IList<Order> orderCollection, [FromBody] AddOrderParam param) =>
{
    if (param is { ProductId.Length: > 0, Count: > 0 })
    {
        orderCollection.Add(new Order(Guid.NewGuid(), param.ProductId, param.Count, DateTime.UtcNow));
        return Results.Ok();
    }

    return Results.BadRequest();
});

app.MapPost("/toggle_collect", ([FromServices] CaptureProcessHolder holder, [FromBody] long toggle) =>
{
    ((toggle == 0) ? (Action)holder.Pause : holder.Resume)();
});

app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
{
    app.Services.GetRequiredService<IAppMetrics>().Startup();
});

await app.RunAsync();

record class AddOrderParam(string ProductId, ushort Count);

record class Order(Guid OrderId, string ProductId, ushort Count, DateTime OrderTime);

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
        var name = configuration.GetValue<string>("METER_NAME") ?? Assembly.GetEntryAssembly()!.GetName().Name!;
        var meter = meterFactory.Create(name, version: "1.0.0");

        _request = meter.CreateCounter<long>(name: "request.count", description: "Counts the number of request");
        _elapsedMs = meter.CreateGauge<long>(name: "request.elapsed", unit: "ms");

        _objectCount = meter.CreateGauge<long>(name: "obj.count");
        _objectSize = meter.CreateGauge<double>(name: "obj.size", unit: "bytes");

        _captureMs = meter.CreateGauge<long>(name: "capture.elapsed", unit: "ms");
        _capture = meter.CreateCounter<long>(name: "capture.count");
        _captureRestart = meter.CreateCounter<long>(name: "capture.restart");
    }

    public void Startup()
    {
        _request.Add(0);
        _objectCount.Record(0);
        _objectSize.Record(0);
        _capture.Add(0);
        _captureRestart.Add(0);
    }

    public void IncrementRequestCount(string name) => _request.Add(1, [new("action", name)]);

    public void LogRequestHandleTime(string name, long ms) => _elapsedMs.Record(ms, [new("action", name)]);

    public void LogHeapObject(TypeInfo info, uint count, ulong size)
    {
        var tags = new KeyValuePair<string, object?>[2]
        {
            new("type", info.Name),
            new("assembly", info.AssemblyName),
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
    void Startup();
    void LogHeapObject(TypeInfo type, uint count, ulong size);
    void LogCaptureTime(long ms);
    void IncrementCaptureCount();
    void IncrementCaptureRestartCount();
}

/// <summary>
/// 因為希望擷取 dump 資料本身的動作不要影響擷取的結果
/// 所以需要透過 subprocess 去執行; 再將指標匯出到獨立的 prometheus port
/// </summary>
internal sealed class CaptureProcessHolder(ILogger<CaptureProcessHolder> logger, IAppMetrics appMetrics) : BackgroundService
{
    private long _pause;
    private Process? _currentProcess;

    private const string KeyArg = "--heap-capture-pid=";

    public static bool TryBuildHeapCaptureApp([NotNullWhen(returnValue: true)] out WebApplication? heapCaptureApp)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.FirstOrDefault(a => a.StartsWith(KeyArg)) is { } pidArg && ulong.TryParse(pidArg[KeyArg.Length..], out var pid))
        {
            var appBuilder = WebApplication.CreateBuilder(args);
            var meterName = appBuilder.Configuration.GetValue<string>("METER_NAME") ?? Assembly.GetEntryAssembly()!.GetName().Name!;
            appBuilder.Logging.ClearProviders().AddSimpleConsole();
            appBuilder.Services
                .AddHostedService(p => ActivatorUtilities.CreateInstance<HeapInfoCapture>(p, pid))
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
        var startInfo = new ProcessStartInfo("DockerAppDemo", [KeyArg + Environment.ProcessId]);
        startInfo.Environment["ASPNETCORE_URLS"] = "http://+:10255";
        startInfo.Environment["ASPNETCORE_METER_NAME"] = Assembly.GetExecutingAssembly().GetName().Name + "-hc";
        startInfo.Environment["DOTNET_gcServer"] = "0";
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

                logger.LogInformation("Capture subprocess exit: {code}", process.ExitCode);

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

public sealed record class TypeInfo(string Name, string AssemblyName)
{
    public static TypeInfo FromClrType(ClrType type) => new(type.Name!, type.Module.AssemblyName!);
}

internal sealed class HeapInfoCapture(ILogger<HeapInfoCapture> logger, IHostEnvironment hostEnvironment, IHostApplicationLifetime lifetime, IAppMetrics appMetrics, ulong pid) : BackgroundService
{
    sealed class Meta(ulong size, uint count)
    {
        public ulong Size { get; set; } = size;
        public uint Count { get; set; } = count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var process = Process.GetProcessById((int)pid);
        var executorPath = await EnsureDumpExecutorAsync(hostEnvironment.ContentRootPath, stoppingToken);
        var dumpfile = Path.Combine(hostEnvironment.ContentRootPath, "HeapInfoCapture.dmp");
        var startInfo = new ProcessStartInfo(executorPath, ["collect", "--type", "Heap", "-p", pid.ToString(), "-o", dumpfile]);

        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        ThreadPool.QueueUserWorkItem(TryExec, startInfo);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                logger.LogInformation("Capture process is exit: {code}, will stop self", process.ExitCode);
                lifetime.StopApplication();
                break;
            }
            await Task.Delay(200, stoppingToken);
        }
    }

    void TryExec(object? status)
    {
        try
        {
            Exec(status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Capture subprocess faulted: {message}", ex.Message);
        }
        Thread.Sleep(TimeSpan.FromSeconds(30));
        ThreadPool.QueueUserWorkItem(TryExec, status);
    }

    readonly Dictionary<TypeInfo, Meta> Type2count = new(EqualityComparer<TypeInfo>.Create(
        equals: (a, b) => a!.Name!.Equals(b!.Name),
        getHashCode: a => a.Name!.GetHashCode()));

    void Exec(object? status)
    {
        var startInfo = (status as ProcessStartInfo)!;
        var dumpfile = startInfo.ArgumentList.Last();

        var sw = ValueStopwatch.StartNew();

        using (var collect = Process.Start(startInfo)!)
        {
            collect.WaitForExit();
        }

        if (!File.Exists(dumpfile))
        {
            return;
        }

        Type2count.Clear();

        ReadDumpFile(dumpfile, Type2count);

        File.Delete(dumpfile);

        foreach (var (info, meta) in Type2count)
        {
            appMetrics.LogHeapObject(info, meta.Count, meta.Size);
            (meta.Size, meta.Count) = (0, 0);
        }

        var captureElapsedMs = (long)sw.GetElapsedTime().TotalMilliseconds;
        appMetrics.LogCaptureTime(captureElapsedMs);
        appMetrics.IncrementCaptureCount();
    }

    private static void ReadDumpFile(string dumpfile, IDictionary<TypeInfo, Meta> type2count)
    {
        using var stream = File.OpenRead(dumpfile);
        using var target = DataTarget.LoadDump(dumpfile, stream);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            if (obj.Type is not { } type || type.Name is not { Length: > 0 })
            {
                continue;
            }

            var info = TypeInfo.FromClrType(type);
            if (!type2count.TryGetValue(info, out var meta))
            {
                meta = new(0, 0);
                type2count.Add(info, meta);
            }

            meta.Size += obj.Size;
            meta.Count += 1;
        }
    }

    private static async ValueTask<string> EnsureDumpExecutorAsync(string folderPath, CancellationToken cancellationToken)
    {
        var executor = Path.Combine(folderPath, "dotnet-dump");
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
