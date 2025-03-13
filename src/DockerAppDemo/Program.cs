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
    .AddSingleton<AppMetrics>();

appBuilder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .ConfigureResource(resource => resource.AddService(serviceName: appBuilder.Environment.ApplicationName))
        .AddMeter(appBuilder.Environment.ApplicationName)
        .AddPrometheusExporter());

#endregion

var app = appBuilder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

#region 註冊給 Prometheus 撈取資料的端點並限制 port

app.MapPrometheusScrapingEndpoint().RequireHost("*:10254");

#endregion

app.MapGet("/action", ([FromServices] AppMetrics metrics) =>
{
    var result =  Results.Ok("Is work !! :D");

#region 在需要的位置對 Counter 進行操作

    metrics.IncrementRequestCount();

#endregion

    return result;
});

await app.RunAsync();

sealed class AppMetrics
{
    private readonly Counter<int> _request;

    public AppMetrics(IHostEnvironment hostEnvironment, IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(name: hostEnvironment.ApplicationName, version: "1.0.0");
        _request = meter.CreateCounter<int>(name: "request.count", description: "Counts the number of request");
    }

    public void IncrementRequestCount() => _request.Add(1);
}
