using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BillingService;

public class BillingService(
    BillingRepository repository,
    IHttpClientFactory httpClientFactory,
    string productsUrl,
    string paymentUrl,
    ILogger<BillingService> logger)
{
    private const double ShippingCost = 25.0;
    private const double TaxRate = 0.2;
    private const string PaymentServiceUnavailable = "payment_service_unavailable";
    private const string PaymentDeclined = "payment declined";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private static readonly ActivitySource ActivitySource = new("billing-service");
    private static readonly Meter Meter = new("billing-service");
    private static readonly Counter<long> CheckoutsTotal =
        Meter.CreateCounter<long>("checkouts.total", description: "Total checkout requests");
    private static readonly Counter<long> CheckoutsDeclined =
        Meter.CreateCounter<long>("checkouts_declined.total", description: "Declined checkouts");
    private static readonly Histogram<double> CheckoutDuration =
        Meter.CreateHistogram<double>("checkout.duration", unit: "ms", description: "Checkout processing duration");

    private enum CheckoutOutcome
    {
        Attempted,
        Approved,
        Declined
    }

    private enum DeclineReason
    {
        SimulatedFailure,
        ValidationFailed,
        ServiceUnavailable,
        BankDeclined
    }

    private HttpClient ProductsClient => httpClientFactory.CreateClient("products");
    private HttpClient PaymentClient => httpClientFactory.CreateClient("payment");

    public SummaryResponse ComputeSummary(SummaryItem[] items)
    {
        var subtotal = items.Sum(i => i.Price * i.Quantity);
        var shipping = items.Length > 0 ? ShippingCost : 0.0;
        var tax = Math.Round(subtotal * TaxRate, 2);
        var total = Math.Round(subtotal + shipping + tax, 2);
        return new SummaryResponse(Math.Round(subtotal, 2), shipping, TaxRate, tax, total);
    }

    public async Task<(List<ValidatedItem> Validated, List<ValidationError> Errors)> ValidateProducts(CartItem[] items)
    {
        using var validateActivity = ActivitySource.StartActivity("validate.stock");
        validateActivity?.SetTag("validation.items_count", items.Length);

        var validated = new List<ValidatedItem>();
        var errors = new List<ValidationError>();

        foreach (var item in items)
        {
            HttpResponseMessage res;
            try
            {
                res = await ProductsClient.GetAsync($"{productsUrl}/products/{item.ProductId}");
            }
            catch
            {
                errors.Add(new ValidationError(item.ProductId, null, "product service unavailable"));
                continue;
            }

            if (!res.IsSuccessStatusCode)
            {
                errors.Add(new ValidationError(item.ProductId, null, "product not found"));
                continue;
            }

            var product = await res.Content.ReadFromJsonAsync<ProductDto>(JsonOpts);
            if (product is null)
            {
                errors.Add(new ValidationError(item.ProductId, null, "product not found"));
                continue;
            }

            if (product.Status != "Active")
            {
                errors.Add(new ValidationError(item.ProductId, product.Name, "product is no longer available"));
                continue;
            }

            if (product.Stock < item.Quantity)
            {
                errors.Add(new ValidationError(item.ProductId, product.Name,
                    $"insufficient stock (requested: {item.Quantity}, available: {product.Stock})"));
                continue;
            }

            validated.Add(new ValidatedItem(product, item.Quantity));
        }

        validateActivity?.SetTag("validation.errors", errors.Count);
        validateActivity?.SetTag("validation.status", errors.Count > 0 ? "failed" : "passed");

        if (errors.Count > 0)
        {
            validateActivity?.AddEvent(new ActivityEvent("validation_failed",
                tags: new ActivityTagsCollection { { "error_count", errors.Count } }));
        }

        return (validated, errors);
    }

    public async Task<(PaymentDto? Data, string? Error)> ProcessPayment(string userId, double amount)
    {
        using var payActivity = ActivitySource.StartActivity("process.payment");
        payActivity?.SetTag("user.id", userId);
        payActivity?.SetTag("payment.total", amount);

        HttpResponseMessage res;
        try
        {
            res = await PaymentClient.PostAsJsonAsync(
                $"{paymentUrl}/payments",
                new { UserId = userId, Amount = amount },
                JsonOpts);
        }
        catch
        {
            payActivity?.SetTag("payment.approved", false);
            payActivity?.SetTag("payment.decline_reason", PaymentServiceUnavailable);
            payActivity?.SetStatus(ActivityStatusCode.Error, PaymentServiceUnavailable);
            payActivity?.AddEvent(new ActivityEvent("payment_service_error",
                tags: new ActivityTagsCollection { { "reason", PaymentServiceUnavailable } }));
            return (null, PaymentServiceUnavailable);
        }

        var data = await res.Content.ReadFromJsonAsync<PaymentDto>(JsonOpts);
        if ((int)res.StatusCode != 201 || data?.Status != "approved")
        {
            payActivity?.SetTag("payment.approved", false);
            return (data, PaymentDeclined);
        }

        payActivity?.SetTag("payment.approved", true);
        return (data, null);
    }

    public async Task UpdateStock(List<ValidatedItem> items)
    {
        using var updateActivity = ActivitySource.StartActivity("update.stock");
        updateActivity?.SetTag("update.items_count", items.Count);

        foreach (var entry in items)
        {
            var newStock = entry.Product.Stock - entry.Quantity;
            await ProductsClient.PatchAsJsonAsync(
                $"{productsUrl}/products/{entry.Product.Id}/stock",
                new { Stock = newStock },
                JsonOpts);
            updateActivity?.AddEvent(new ActivityEvent("stock_update_performed",
                tags: new ActivityTagsCollection { { "product_id", entry.Product.Id }, { "new_stock", newStock } }));
        }
    }

    public async Task<(CheckoutResponse? Result, object? Error, int StatusCode)> Checkout(string userId, CartItem[] cartItems)
    {
        var startTime = Stopwatch.GetTimestamp();

        using var checkoutActivity = ActivitySource.StartActivity("process.checkout");
        checkoutActivity?.SetTag("user_id", userId);
        checkoutActivity?.SetTag("checkout.items_count", cartItems.Length);

        IncreaseCheckoutTotal(CheckoutOutcome.Attempted);
        logger.LogInformation("checkout_started {UserId} {ItemCount}", userId, cartItems.Length);

        await ThrowIfSimulatedFailure(checkoutActivity, userId, startTime);
        await ApplyMemoryLeakSimulation(checkoutActivity, userId);

        var (validated, errors) = await ValidateProducts(cartItems);
        if (errors.Count > 0)
            return DeclineForValidation(checkoutActivity, userId, startTime, errors);

        var total = ComputeOrderTotal(validated);

        var (payData, payError) = await ProcessPayment(userId, total);
        if (payError is not null)
            return DeclineForPayment(userId, startTime, total, payData, payError);

        await UpdateStock(validated);
        var orderId = SaveOrder(userId, total, validated, payData?.Id);

        return ApproveCheckout(checkoutActivity, userId, startTime, total, orderId, payData, validated.Count);
    }

    private async Task ThrowIfSimulatedFailure(Activity? activity, string userId, long startTime)
    {
        var failPct = await FeatureFlags.Client.GetDoubleValueAsync("billingCheckoutFailure", 0.0);
        if (failPct <= 0 || Random.Shared.NextDouble() >= failPct)
            return;

        activity?.SetTag("feature_flag.checkout_failure_rate", failPct);
        activity?.AddEvent(new ActivityEvent("checkout_failure_simulated",
            tags: new ActivityTagsCollection { { "failure_rate", failPct } }));
        activity?.SetStatus(ActivityStatusCode.Error, "simulated checkout failure");

        logger.LogError("checkout_simulated_failure {UserId} {FailureRate}", userId, failPct);
        RecordDecline(startTime, DeclineReason.SimulatedFailure);

        throw new InvalidOperationException($"simulated checkout failure (rate={failPct})");
    }

    private async Task ApplyMemoryLeakSimulation(Activity? activity, string userId)
    {
        var leakLevel = await FeatureFlags.Client.GetIntegerValueAsync("billingMemoryLeak", 0);
        MemoryLeakSimulator.Allocate(leakLevel);
        if (leakLevel <= 0)
            return;

        activity?.SetTag("feature_flag.memory_leak_level", leakLevel);
        activity?.SetTag("memory_leak.retained_chunks", MemoryLeakSimulator.RetainedChunks);
        activity?.AddEvent(new ActivityEvent("memory_leak_simulated",
            tags: new ActivityTagsCollection { { "chunks_allocated", leakLevel }, { "total_retained", MemoryLeakSimulator.RetainedChunks } }));

        logger.LogWarning("checkout_memory_leak_simulated {UserId} {LeakLevel} {RetainedChunks}",
            userId, leakLevel, MemoryLeakSimulator.RetainedChunks);
    }

    private static double ComputeOrderTotal(List<ValidatedItem> validated)
    {
        using var totalActivity = ActivitySource.StartActivity("compute.total");
        var subtotal = validated.Sum(e => e.Product.Price * e.Quantity);
        var tax = Math.Round(subtotal * TaxRate, 2);
        var total = Math.Round(subtotal + ShippingCost + tax, 2);
        totalActivity?.SetTag("subtotal", subtotal);
        totalActivity?.SetTag("tax", tax);
        totalActivity?.SetTag("shipping", ShippingCost);
        totalActivity?.SetTag("total", total);
        return total;
    }

    private string SaveOrder(string userId, double total, List<ValidatedItem> validated, string? paymentId)
    {
        using var recordActivity = ActivitySource.StartActivity("save.order");
        var orderItems = validated.Select(e => new OrderItemInput(
            e.Product.Id, e.Product.Name, e.Product.ImageUrl ?? "", e.Product.Price, e.Quantity));
        var orderId = repository.InsertOrder(userId, total, paymentId, orderItems);
        recordActivity?.SetTag("order_id", orderId);
        recordActivity?.SetTag("save.items_updated", validated.Count);
        return orderId;
    }

    private (CheckoutResponse? Result, object? Error, int StatusCode) DeclineForValidation(
        Activity? activity, string userId, long startTime, List<ValidationError> errors)
    {
        logger.LogWarning("checkout_validation_failed {UserId} {ErrorCount}", userId, errors.Count);
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.AddEvent(new ActivityEvent("checkout_validation_failed"));
        RecordDecline(startTime, DeclineReason.ValidationFailed);
        return (null, new { Error = "checkout validation failed", Details = errors }, 409);
    }

    private (CheckoutResponse? Result, object? Error, int StatusCode) DeclineForPayment(
        string userId, long startTime, double total, PaymentDto? payData, string payError)
    {
        logger.LogWarning("checkout_payment_failed {UserId} {Total} {Error}", userId, total, payError);

        var isTechnicalError = payError == PaymentServiceUnavailable;
        var reason = isTechnicalError ? DeclineReason.ServiceUnavailable : DeclineReason.BankDeclined;
        RecordDecline(startTime, reason);

        return (null, new
        {
            Error = payError,
            PaymentId = payData?.PaymentId ?? payData?.Id,
            Details = payData?.Error ?? (isTechnicalError ? payError : "The bank declined the transaction")
        }, isTechnicalError ? 502 : 402);
    }

    private (CheckoutResponse? Result, object? Error, int StatusCode) ApproveCheckout(
        Activity? activity, string userId, long startTime, double total, string orderId, PaymentDto? payData, int itemsCount)
    {
        activity?.SetTag("order_id", orderId);
        activity?.SetTag("outcome", ToTagValue(CheckoutOutcome.Approved));

        IncreaseCheckoutTotal(CheckoutOutcome.Approved);
        RecordDuration(startTime, CheckoutOutcome.Approved);

        logger.LogInformation("checkout_completed {UserId} {OrderId} {Total}", userId, orderId, total);
        return (new CheckoutResponse("ok", orderId, payData?.Id, total, itemsCount), null, 200);
    }

    private static void RecordDecline(long startTime, DeclineReason reason)
    {
        IncreaseCheckoutTotal(CheckoutOutcome.Declined);
        RecordDuration(startTime, CheckoutOutcome.Declined);
        CheckoutsDeclined.Add(1, new TagList { { "reason", ToTagValue(reason) } });
    }

    public IEnumerable<OrderResult> GetUserOrders(string userId) =>
        repository.FindOrdersByUser(userId).Select(o => FormatOrder(o));

    public IEnumerable<OrderResult> GetAllOrders() =>
        repository.FindAllOrders().Select(o => FormatOrder(o, includeUser: true));

    private OrderResult FormatOrder(OrderRow order, bool includeUser = false)
    {
        var items = repository.FindOrderItems(order.Id)
            .Select(i => new OrderItemResult(i.ProductId, i.ProductName, i.ProductImageUrl, i.Price, i.Quantity))
            .ToArray();
        return new OrderResult(order.Id, order.Total, order.CreatedAt, items, includeUser ? order.UserId : null);
    }

    private static void IncreaseCheckoutTotal(CheckoutOutcome outcome)
    {
        CheckoutsTotal.Add(1, new TagList { { "status", ToTagValue(outcome) } });
    }

    private static void RecordDuration(long startTimestamp, CheckoutOutcome outcome)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        CheckoutDuration.Record(elapsed.TotalMilliseconds, new TagList { { "outcome", ToTagValue(outcome) } });
    }

    private static string ToTagValue(CheckoutOutcome outcome) => outcome switch
    {
        CheckoutOutcome.Attempted => "attempted",
        CheckoutOutcome.Approved => "approved",
        CheckoutOutcome.Declined => "declined",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static string ToTagValue(DeclineReason reason) => reason switch
    {
        DeclineReason.SimulatedFailure => "simulated_failure",
        DeclineReason.ValidationFailed => "validation_failed",
        DeclineReason.ServiceUnavailable => "service_unavailable",
        DeclineReason.BankDeclined => "bank_declined",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}
