using Microsoft.Data.Sqlite;

namespace BillingService;

public class BillingRepository(string dbPath)
{
    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    public void InitDb()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS orders (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                total REAL NOT NULL,
                payment_id TEXT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS order_items (
                id TEXT PRIMARY KEY,
                order_id TEXT NOT NULL,
                product_id TEXT NOT NULL,
                product_name TEXT NOT NULL,
                product_image_url TEXT DEFAULT '',
                price REAL NOT NULL,
                quantity INTEGER NOT NULL,
                FOREIGN KEY (order_id) REFERENCES orders(id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public string InsertOrder(string userId, double total, string? paymentId, IEnumerable<OrderItemInput> items)
    {
        var orderId = Guid.NewGuid().ToString();
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO orders (id, user_id, total, payment_id) VALUES (@id, @userId, @total, @paymentId)";
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@total", total);
        cmd.Parameters.AddWithValue("@paymentId", (object?)paymentId ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        foreach (var item in items)
        {
            using var ic = conn.CreateCommand();
            ic.CommandText = """
                INSERT INTO order_items (id, order_id, product_id, product_name, product_image_url, price, quantity)
                VALUES (@id, @orderId, @productId, @productName, @productImageUrl, @price, @quantity)
                """;
            ic.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            ic.Parameters.AddWithValue("@orderId", orderId);
            ic.Parameters.AddWithValue("@productId", item.ProductId);
            ic.Parameters.AddWithValue("@productName", item.ProductName);
            ic.Parameters.AddWithValue("@productImageUrl", item.ProductImageUrl ?? "");
            ic.Parameters.AddWithValue("@price", item.Price);
            ic.Parameters.AddWithValue("@quantity", item.Quantity);
            ic.ExecuteNonQuery();
        }

        tx.Commit();
        return orderId;
    }

    public IEnumerable<OrderRow> FindOrdersByUser(string userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, total, payment_id, created_at FROM orders WHERE user_id = @userId ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@userId", userId);
        return ReadOrders(cmd);
    }

    public IEnumerable<OrderRow> FindAllOrders()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, total, payment_id, created_at FROM orders ORDER BY created_at DESC";
        return ReadOrders(cmd);
    }

    public IEnumerable<OrderItemRow> FindOrderItems(string orderId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT product_id, product_name, product_image_url, price, quantity FROM order_items WHERE order_id = @orderId";
        cmd.Parameters.AddWithValue("@orderId", orderId);
        var items = new List<OrderItemRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(new OrderItemRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.GetInt32(4)
            ));
        return items;
    }

    private static List<OrderRow> ReadOrders(SqliteCommand cmd)
    {
        var rows = new List<OrderRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new OrderRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)
            ));
        return rows;
    }
}
