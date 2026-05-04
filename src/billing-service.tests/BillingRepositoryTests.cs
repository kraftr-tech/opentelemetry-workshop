using BillingService;
using Xunit;

namespace BillingService.Tests;

public class BillingRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BillingRepository _repo;

    public BillingRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"billing_test_{Guid.NewGuid()}.db");
        _repo = new BillingRepository(_dbPath);
        _repo.InitDb();
    }

    public void Dispose() => File.Delete(_dbPath);

    [Fact]
    public void InitDb_CreatesTables()
    {
        var orderId = _repo.InsertOrder("user1", 100.0, "pay1", [
            new OrderItemInput("prod1", "Produit A", "http://img", 50.0, 1),
            new OrderItemInput("prod2", "Produit B", "", 50.0, 1),
        ]);
        Assert.NotEmpty(orderId);
    }

    [Fact]
    public void InsertOrder_ReturnsNewGuid()
    {
        var id1 = _repo.InsertOrder("user1", 50.0, "pay1", [
            new OrderItemInput("prod1", "A", "", 50.0, 1)
        ]);
        var id2 = _repo.InsertOrder("user1", 75.0, "pay2", [
            new OrderItemInput("prod2", "B", "", 75.0, 1)
        ]);

        Assert.NotEqual(id1, id2);
        Assert.True(Guid.TryParse(id1, out _));
        Assert.True(Guid.TryParse(id2, out _));
    }

    [Fact]
    public void FindOrdersByUser_ReturnsOnlyUserOrders()
    {
        _repo.InsertOrder("userA", 10.0, null, [new OrderItemInput("p1", "P1", "", 10.0, 1)]);
        _repo.InsertOrder("userB", 20.0, null, [new OrderItemInput("p2", "P2", "", 20.0, 1)]);

        var orders = _repo.FindOrdersByUser("userA").ToList();

        Assert.Single(orders);
        Assert.Equal("userA", orders[0].UserId);
        Assert.Equal(10.0, orders[0].Total);
    }

    [Fact]
    public void FindAllOrders_ReturnsAllOrders()
    {
        _repo.InsertOrder("userA", 10.0, null, [new OrderItemInput("p1", "P1", "", 10.0, 1)]);
        _repo.InsertOrder("userB", 20.0, null, [new OrderItemInput("p2", "P2", "", 20.0, 1)]);

        var orders = _repo.FindAllOrders().ToList();

        Assert.Equal(2, orders.Count);
    }

    [Fact]
    public void FindOrderItems_ReturnsItemsForOrder()
    {
        var orderId = _repo.InsertOrder("user1", 150.0, "pay1", [
            new OrderItemInput("prod1", "Produit A", "http://img/a.png", 100.0, 1),
            new OrderItemInput("prod2", "Produit B", "", 50.0, 2),
        ]);

        var items = _repo.FindOrderItems(orderId).ToList();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.ProductId == "prod1" && i.Price == 100.0 && i.Quantity == 1);
        Assert.Contains(items, i => i.ProductId == "prod2" && i.Price == 50.0 && i.Quantity == 2);
    }

    [Fact]
    public void FindOrdersByUser_ReturnsDescendingOrder()
    {
        _repo.InsertOrder("user1", 10.0, null, [new OrderItemInput("p1", "P1", "", 10.0, 1)]);
        System.Threading.Thread.Sleep(1100);
        _repo.InsertOrder("user1", 20.0, null, [new OrderItemInput("p2", "P2", "", 20.0, 1)]);

        var orders = _repo.FindOrdersByUser("user1").ToList();

        Assert.True(string.Compare(orders[0].CreatedAt, orders[1].CreatedAt, StringComparison.Ordinal) > 0);
    }
}
