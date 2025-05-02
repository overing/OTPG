using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Services.AddOpenApi();

appBuilder.Services.AddSingleton<IList<Order>, List<Order>>();

var app = appBuilder.Build();

app.MapOpenApi();
app.MapScalarApiReference(endpointPrefix: "/");

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
    var pid = Environment.ProcessId;
    var filename = $"{DateTime.Now:yyMMdd-HHmmss}.dmp";
    System.Diagnostics.Process.Start(startInfo: new("dotnet-dump", arguments: $"collect -p {pid} -o {filename}"));
    return Results.Ok(filename);
});

await app.RunAsync(url: "http://localhost:5000/");

record class AddOrderParam(string ProductId = "product#1001", ushort Count = 1);

record class Order(Guid OrderId, string ProductId, ushort Count, DateTime OrderTime);
