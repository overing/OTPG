using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Services.AddOpenApi();

appBuilder.Services.AddSingleton<OrderService>();

var app = appBuilder.Build();

app.MapOpenApi();
app.MapScalarApiReference(endpointPrefix: "/");

app.MapPost("/api/order", ([FromServices] OrderService orderService, [FromBody] AddOrderParam param) =>
{
    if (param is { ProductId.Length: > 0, Count: > 0 })
    {
        orderService.AddOrder(param);
        return Results.Ok($"{orderService.Count} orders added.");
    }

    return Results.BadRequest();
});

await app.RunAsync(url: "http://localhost:5000/");

record class AddOrderParam(
    [Required(AllowEmptyStrings = false, ErrorMessage = "產品編號是必須的")]
    string ProductId = "product#1001",

    [Range(minimum: 1, maximum: ushort.MaxValue, ErrorMessage = "數量必須大於 0")]
    ushort Count = 1
);

[DebuggerDisplay("#{OrderId}, Product#{ProductId} x{Count} at {OrderTime}")]
record class Order(Guid OrderId, string ProductId, ushort Count, DateTime OrderTime);

sealed class OrderService
{
    private readonly List<Order> _orders = [];

    public int Count => _orders.Count;

    public void AddOrder(AddOrderParam param) => _orders.Add(new(Guid.NewGuid(), param.ProductId, param.Count, DateTime.UtcNow));
}
