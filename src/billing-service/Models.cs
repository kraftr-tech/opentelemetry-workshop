namespace BillingService;

// --- Requêtes API entrantes ---
public record SummaryItem(double Price, int Quantity);
public record SummaryRequest(SummaryItem[]? Items);
public record CartItem(string ProductId, int Quantity);
public record CheckoutRequest(string? UserId, CartItem[]? Items);

// --- Réponses API sortantes ---
public record SummaryResponse(double Subtotal, double Shipping, double TaxRate, double Tax, double Total);
public record CheckoutResponse(string Status, string OrderId, string? PaymentId, double Total, int Items);
public record OrderItemResult(string ProductId, string ProductName, string ProductImageUrl, double Price, int Quantity);
public record OrderResult(string Id, double Total, string CreatedAt, OrderItemResult[] Items, string? UserId = null);

// --- DTOs internes (services externes) ---
public record ProductDto(string Id, string Name, double Price, int Stock, string Status, string? ImageUrl);
public record PaymentDto(string? Id, string? PaymentId, string? Status, string? Error);
public record ValidatedItem(ProductDto Product, int Quantity);
public record ValidationError(string ProductId, string? Name, string Error);

// --- Couche repository ---
public record OrderRow(string Id, string UserId, double Total, string? PaymentId, string CreatedAt);
public record OrderItemRow(string ProductId, string ProductName, string ProductImageUrl, double Price, int Quantity);
public record OrderItemInput(string ProductId, string ProductName, string? ProductImageUrl, double Price, int Quantity);
