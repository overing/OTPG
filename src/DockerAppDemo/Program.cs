using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Services.AddOpenApi();

appBuilder.Services.AddSingleton<ConcurrentQueue<MyData>>();

var app = appBuilder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapPost("/enqueue", ([FromServices] ConcurrentQueue<MyData> queue, [FromBody] ushort count) =>
{
    var sw = Stopwatch.StartNew();

    for (var i = 0; i < count; i++)
    {
        queue.Enqueue(new() { Id = Guid.NewGuid(), Value = Random.Shared.Next(1000) });
    }

    return Results.Ok($"enqueue guids x{count} done");
});

await app.RunAsync();

class MyData
{
    public Guid Id { get; set; }
    public int Value { get; set; }
}
