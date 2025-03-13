using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Scalar.AspNetCore;

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

    metrics.IncrementRequestCount();
    metrics.LogRequestHandleTime("/action", sw.ElapsedMilliseconds);

#endregion

    return result;
});

await app.RunAsync();

sealed class AppMetrics : IAppMetrics
{
    private readonly Counter<long> _request;
    private readonly Gauge<long> _elapsedMs;

    public AppMetrics(IHostEnvironment hostEnvironment, IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(name: hostEnvironment.ApplicationName, version: "1.0.0");
        _request = meter.CreateCounter<long>(name: "request.count", description: "Counts the number of request");
        _elapsedMs = meter.CreateGauge<long>(name: "request.elapsed_time", unit: "ms");
    }

    public void IncrementRequestCount() => _request.Add(1);

    public void LogRequestHandleTime(string name, long ms) => _elapsedMs.Record(ms, [new("action", name)]);
}

interface IAppMetrics
{
    void IncrementRequestCount();
    void LogRequestHandleTime(string name, long ms);
}

sealed class FakeAccess(HttpClient client) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1000), stoppingToken);

            _ = client.GetAsync("action", stoppingToken);
        }
    }
}
