using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Services.AddOpenApi();

appBuilder.Services.AddSingleton<IList<Order>, List<Order>>();

var app = appBuilder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapPost("/api/add_order", ([FromServices] IList<Order> orderCollection, [FromBody] AddOrderParam param) =>
{
    if (param is { ProductId.Length: > 0, Count: > 0 })
    {
        orderCollection.Add(new(Guid.NewGuid(), param.ProductId, param.Count, DateTime.UtcNow));
        return Results.Ok($"{orderCollection.Count} orders added.");
    }

    return Results.BadRequest();
});

app.MapPost("/api/dump", () =>
{
    var filename = $"{DateTime.Now:yyMMdd-HHmmss}.dmp";
    Process.Start(startInfo: new("dotnet-dump", arguments: $"collect -p {Environment.ProcessId} -o {filename}"));
    return Results.Ok(filename);
});

app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
{
    Process.Start(startInfo: new("http://localhost:5000/scalar/") { UseShellExecute = true });
});

await app.RunAsync("http://localhost:5000/");

record class AddOrderParam(string ProductId = "product#1001", ushort Count = 1);

record class Order(Guid OrderId, string ProductId, ushort Count, DateTime OrderTime);
