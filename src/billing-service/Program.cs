using System.Text.Json;
using System.Text.Json.Serialization;
using BillingService;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    opts.SerializerOptions.PropertyNameCaseInsensitive = true;
    opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var productsUrl = Environment.GetEnvironmentVariable("PRODUCTS_SERVICE_URL") ?? "http://products-service:8002";
var paymentUrl = Environment.GetEnvironmentVariable("PAYMENT_SERVICE_URL") ?? "http://payment-service:8003";
var dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "/data/billing.db";

builder.Services.AddHttpClient("products", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("payment", c => c.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddSingleton(_ => new BillingRepository(dbPath));
builder.Services.AddSingleton(sp => new BillingService.BillingService(
    sp.GetRequiredService<BillingRepository>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    productsUrl,
    paymentUrl,
    sp.GetRequiredService<ILogger<BillingService.BillingService>>()
));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(
            serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "billing-service",
            serviceVersion: Environment.GetEnvironmentVariable("OTEL_SERVICE_VERSION") ?? "1.0.0"))
    .WithTracing(b => b
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(b => b
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.ParseStateValues = true;
    o.AddOtlpExporter();
});

var app = builder.Build();

await BillingService.FeatureFlags.InitAsync();
app.Services.GetRequiredService<BillingRepository>().InitDb();

app.MapGet("/health", () => Results.Ok(new { Status = "ok" }));

app.MapPost("/summary", (SummaryRequest? req, BillingService.BillingService svc) =>
{
    var items = req?.Items ?? [];
    return Results.Ok(svc.ComputeSummary(items));
});

app.MapPost("/checkout", async (CheckoutRequest req, BillingService.BillingService svc) =>
{
    if (string.IsNullOrEmpty(req.UserId) || req.Items == null || req.Items.Length == 0)
        return Results.BadRequest(new { Error = "user_id and items are required" });

    var (result, error, statusCode) = await svc.Checkout(req.UserId, req.Items);
    return statusCode switch
    {
        200 => Results.Ok(result),
        _ => Results.Json(error, statusCode: statusCode)
    };
});

app.MapGet("/orders/{userId}", (string userId, BillingService.BillingService svc) =>
    Results.Ok(svc.GetUserOrders(userId)));

app.MapGet("/orders", (BillingService.BillingService svc) =>
    Results.Ok(svc.GetAllOrders()));

app.Run("http://0.0.0.0:8004");

public partial class Program { }
