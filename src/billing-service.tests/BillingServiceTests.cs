using System.Net;
using System.Text.Json;
using BillingService;
using Moq;
using RichardSzalay.MockHttp;
using Xunit;
using BillingSvc = BillingService.BillingService;

namespace BillingService.Tests;

public class BillingServiceTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private static BillingSvc MakeService(
        MockHttpMessageHandler mockHttp,
        BillingRepository? repo = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("products")).Returns(mockHttp.ToHttpClient());
        factory.Setup(f => f.CreateClient("payment")).Returns(mockHttp.ToHttpClient());
        repo ??= MakeRepo();
        return new BillingSvc(repo, factory.Object, "http://products", "http://payment");
    }

    private static BillingRepository MakeRepo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"svc_test_{Guid.NewGuid()}.db");
        var repo = new BillingRepository(path);
        repo.InitDb();
        return repo;
    }

    // --- ComputeSummary ---

    [Fact]
    public void ComputeSummary_WithItems_ReturnsCorrectTotals()
    {
        var svc = MakeService(new MockHttpMessageHandler());

        var result = svc.ComputeSummary([
            new SummaryItem(100.0, 2),
            new SummaryItem(50.0, 1)
        ]);

        Assert.Equal(250.0, result.Subtotal);
        Assert.Equal(25.0, result.Shipping);
        Assert.Equal(0.2, result.TaxRate);
        Assert.Equal(50.0, result.Tax);
        Assert.Equal(325.0, result.Total);
    }

    [Fact]
    public void ComputeSummary_WithNoItems_ReturnsZeroShipping()
    {
        var svc = MakeService(new MockHttpMessageHandler());

        var result = svc.ComputeSummary([]);

        Assert.Equal(0.0, result.Subtotal);
        Assert.Equal(0.0, result.Shipping);
        Assert.Equal(0.0, result.Total);
    }

    // --- ValidateProducts ---

    [Fact]
    public async Task ValidateProducts_AllValid_ReturnsValidatedItems()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("http://products/products/prod1")
            .Respond("application/json", JsonSerializer.Serialize(
                new { id = "prod1", name = "Produit A", price = 50.0, stock = 10, status = "Active", image_url = "http://img/a.png" }));

        var svc = MakeService(mockHttp);
        var (validated, errors) = await svc.ValidateProducts([new CartItem("prod1", 2)]);

        Assert.Empty(errors);
        Assert.Single(validated);
        Assert.Equal("prod1", validated[0].Product.Id);
        Assert.Equal(2, validated[0].Quantity);
    }

    [Fact]
    public async Task ValidateProducts_ProductNotFound_ReturnsError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("http://products/products/missing")
            .Respond(System.Net.HttpStatusCode.NotFound);

        var svc = MakeService(mockHttp);
        var (validated, errors) = await svc.ValidateProducts([new CartItem("missing", 1)]);

        Assert.Empty(validated);
        Assert.Single(errors);
        Assert.Equal("product not found", errors[0].Error);
    }

    [Fact]
    public async Task ValidateProducts_InactiveProduct_ReturnsError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("http://products/products/prod1")
            .Respond("application/json", JsonSerializer.Serialize(
                new { id = "prod1", name = "Old", price = 10.0, stock = 5, status = "Inactive", image_url = "" }));

        var svc = MakeService(mockHttp);
        var (validated, errors) = await svc.ValidateProducts([new CartItem("prod1", 1)]);

        Assert.Empty(validated);
        Assert.Contains("no longer available", errors[0].Error);
    }

    [Fact]
    public async Task ValidateProducts_InsufficientStock_ReturnsError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("http://products/products/prod1")
            .Respond("application/json", JsonSerializer.Serialize(
                new { id = "prod1", name = "Produit A", price = 50.0, stock = 1, status = "Active", image_url = "" }));

        var svc = MakeService(mockHttp);
        var (validated, errors) = await svc.ValidateProducts([new CartItem("prod1", 5)]);

        Assert.Empty(validated);
        Assert.Contains("insufficient stock", errors[0].Error);
    }

    [Fact]
    public async Task ValidateProducts_ServiceUnavailable_ReturnsError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("http://products/products/prod1")
            .Throw(new HttpRequestException("connection refused"));

        var svc = MakeService(mockHttp);
        var (validated, errors) = await svc.ValidateProducts([new CartItem("prod1", 1)]);

        Assert.Empty(validated);
        Assert.Contains("unavailable", errors[0].Error);
    }

    // --- ProcessPayment ---

    [Fact]
    public async Task ProcessPayment_Approved_ReturnsPaymentData()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "http://payment/payments")
            .Respond(System.Net.HttpStatusCode.Created, "application/json",
                JsonSerializer.Serialize(new { id = "pay-uuid", status = "approved" }));

        var svc = MakeService(mockHttp);
        var (data, error) = await svc.ProcessPayment("user1", 145.0);

        Assert.Null(error);
        Assert.Equal("pay-uuid", data!.Id);
    }

    [Fact]
    public async Task ProcessPayment_Declined_ReturnsError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "http://payment/payments")
            .Respond(System.Net.HttpStatusCode.PaymentRequired, "application/json",
                JsonSerializer.Serialize(new { id = "pay-uuid", status = "declined", error = "Insufficient funds" }));

        var svc = MakeService(mockHttp);
        var (data, error) = await svc.ProcessPayment("user1", 145.0);

        Assert.Equal("payment declined", error);
        Assert.NotNull(data);
    }

    [Fact]
    public async Task ProcessPayment_ServiceUnavailable_ReturnsError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "http://payment/payments")
            .Throw(new HttpRequestException("connection refused"));

        var svc = MakeService(mockHttp);
        var (data, error) = await svc.ProcessPayment("user1", 145.0);

        Assert.Null(data);
        Assert.Equal("payment service unavailable", error);
    }

    // --- Checkout ---

    private static string ProductJson(string id, string name, double price, int stock, string status = "Active") =>
        JsonSerializer.Serialize(new { id, name, price, stock, status, image_url = "" });

    [Fact]
    public async Task Checkout_Success_ReturnsOrderId()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, "http://products/products/prod1")
            .Respond("application/json", ProductJson("prod1", "A", 50.0, 10));
        mockHttp.When(HttpMethod.Post, "http://payment/payments")
            .Respond(System.Net.HttpStatusCode.Created, "application/json",
                JsonSerializer.Serialize(new { id = "pay-uuid", status = "approved" }));
        mockHttp.When(HttpMethod.Patch, "http://products/products/prod1/stock")
            .Respond(System.Net.HttpStatusCode.OK);

        var svc = MakeService(mockHttp);
        var (result, error, code) = await svc.Checkout("user1", [new CartItem("prod1", 2)]);

        Assert.Equal(200, code);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal("ok", result.Status);
        Assert.Equal("pay-uuid", result.PaymentId);
        Assert.Equal(1, result.Items);
        // total = 100 + 20 (tax 20%) + 25 (shipping) = 145
        Assert.Equal(145.0, result.Total);
    }

    [Fact]
    public async Task Checkout_StockError_Returns409()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, "http://products/products/prod1")
            .Respond("application/json", ProductJson("prod1", "A", 50.0, 1));

        var svc = MakeService(mockHttp);
        var (result, error, code) = await svc.Checkout("user1", [new CartItem("prod1", 5)]);

        Assert.Equal(409, code);
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Checkout_PaymentDeclined_Returns402()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, "http://products/products/prod1")
            .Respond("application/json", ProductJson("prod1", "A", 50.0, 10));
        mockHttp.When(HttpMethod.Post, "http://payment/payments")
            .Respond(System.Net.HttpStatusCode.PaymentRequired, "application/json",
                JsonSerializer.Serialize(new { id = "pay-uuid", status = "declined", error = "Insufficient funds" }));

        var svc = MakeService(mockHttp);
        var (result, error, code) = await svc.Checkout("user1", [new CartItem("prod1", 1)]);

        Assert.Equal(402, code);
        Assert.Null(result);
    }

    [Fact]
    public async Task Checkout_PaymentUnavailable_Returns502()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, "http://products/products/prod1")
            .Respond("application/json", ProductJson("prod1", "A", 50.0, 10));
        mockHttp.When(HttpMethod.Post, "http://payment/payments")
            .Throw(new HttpRequestException("connection refused"));

        var svc = MakeService(mockHttp);
        var (result, error, code) = await svc.Checkout("user1", [new CartItem("prod1", 1)]);

        Assert.Equal(502, code);
        Assert.Null(result);
    }

    // --- GetOrders ---

    [Fact]
    public void GetUserOrders_ReturnsFormattedOrders()
    {
        var repo = MakeRepo();
        repo.InsertOrder("user1", 100.0, "pay1", [
            new OrderItemInput("p1", "Produit A", "http://img/a.png", 100.0, 1)
        ]);
        var svc = MakeService(new MockHttpMessageHandler(), repo);

        var orders = svc.GetUserOrders("user1").ToList();

        Assert.Single(orders);
        Assert.Equal(100.0, orders[0].Total);
        Assert.Single(orders[0].Items);
        Assert.Equal("p1", orders[0].Items[0].ProductId);
        Assert.Null(orders[0].UserId);
    }

    [Fact]
    public void GetAllOrders_IncludesUserId()
    {
        var repo = MakeRepo();
        repo.InsertOrder("userA", 50.0, null, [new OrderItemInput("p1", "A", "", 50.0, 1)]);
        repo.InsertOrder("userB", 75.0, null, [new OrderItemInput("p2", "B", "", 75.0, 1)]);
        var svc = MakeService(new MockHttpMessageHandler(), repo);

        var orders = svc.GetAllOrders().ToList();

        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.UserId));
    }
}
