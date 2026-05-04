using System.Net.Http.Json;
using System.Text.Json;

namespace BillingService;

public class BillingService(
    BillingRepository repository,
    IHttpClientFactory httpClientFactory,
    string productsUrl,
    string paymentUrl)
{
    private const double ShippingCost = 25.0;
    private const double TaxRate = 0.2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

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
            if (product is null) { errors.Add(new ValidationError(item.ProductId, null, "product not found")); continue; }

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

        return (validated, errors);
    }

    public async Task<(PaymentDto? Data, string? Error)> ProcessPayment(string userId, double amount)
    {
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
            return (null, "payment service unavailable");
        }

        var data = await res.Content.ReadFromJsonAsync<PaymentDto>(JsonOpts);
        if ((int)res.StatusCode != 201 || data?.Status != "approved")
            return (data, "payment declined");

        return (data, null);
    }

    public async Task UpdateStock(List<ValidatedItem> items)
    {
        foreach (var entry in items)
        {
            var newStock = entry.Product.Stock - entry.Quantity;
            await ProductsClient.PatchAsJsonAsync(
                $"{productsUrl}/products/{entry.Product.Id}/stock",
                new { Stock = newStock },
                JsonOpts);
        }
    }

    public async Task<(CheckoutResponse? Result, object? Error, int StatusCode)> Checkout(string userId, CartItem[] cartItems)
    {
        var failPct = await FeatureFlags.Client.GetDoubleValueAsync("billingCheckoutFailure", 0.0);
        if (failPct > 0 && Random.Shared.NextDouble() < failPct)
        {
            throw new InvalidOperationException(
                $"simulated checkout failure (rate={failPct})");
        }

        var leakLevel = await FeatureFlags.Client.GetIntegerValueAsync("billingMemoryLeak", 0);
        MemoryLeakSimulator.Allocate(leakLevel);

        var (validated, errors) = await ValidateProducts(cartItems);
        if (errors.Count > 0)
            return (null, new { Error = "checkout validation failed", Details = errors }, 409);

        var subtotal = validated.Sum(e => e.Product.Price * e.Quantity);
        var tax = Math.Round(subtotal * TaxRate, 2);
        var total = Math.Round(subtotal + ShippingCost + tax, 2);

        var (payData, payError) = await ProcessPayment(userId, total);
        if (payError is not null)
        {
            var statusCode = payError == "payment service unavailable" ? 502 : 402;
            var resolvedPayId = payData?.PaymentId ?? payData?.Id;
            return (null, new
            {
                Error = payError,
                PaymentId = resolvedPayId,
                Details = payData?.Error ?? (payError == "payment service unavailable" ? payError : "The bank declined the transaction")
            }, statusCode);
        }

        await UpdateStock(validated);

        var orderItems = validated.Select(e => new OrderItemInput(
            e.Product.Id, e.Product.Name, e.Product.ImageUrl ?? "", e.Product.Price, e.Quantity
        ));
        var orderId = repository.InsertOrder(userId, total, payData?.Id, orderItems);

        return (new CheckoutResponse("ok", orderId, payData?.Id, total, validated.Count), null, 200);
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
}
