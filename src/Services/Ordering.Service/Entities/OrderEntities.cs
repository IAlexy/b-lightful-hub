namespace Ordering.Service.Entities;

/// <summary>
/// SQL Database Entity representing a B2B Customer Company (1 Customer -> N Orders).
/// Mapped to the "Customers" table via EF Core.
/// </summary>
public class CustomerEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CompanyName { get; set; } = string.Empty;
    public string VatNumber { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string BillingAddress { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation property: 1 Customer has many Orders
    public ICollection<OrderEntity> Orders { get; set; } = new List<OrderEntity>();
}

/// <summary>
/// SQL Database Entity representing a B2B Purchase Order (1 Order -> N OrderLines, belongs to 1 Customer).
/// Mapped to the "Orders" table via EF Core.
/// </summary>
public class OrderEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNumber { get; set; } = string.Empty;

    // Foreign Key & Navigation to CustomerEntity
    public Guid CustomerId { get; set; }
    public CustomerEntity Customer { get; set; } = null!;

    public int PaymentTermsDays { get; set; } = 30;
    public string ShippingDestination { get; set; } = string.Empty;
    public string Status { get; set; } = "Confirmed";
    public decimal TotalAmountHt { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation property: 1 Order has many OrderLines
    public ICollection<OrderLineEntity> Lines { get; set; } = new List<OrderLineEntity>();
}

/// <summary>
/// SQL Database Entity representing an individual line item of a B2B Order.
/// Mapped to the "OrderLines" table via EF Core.
/// </summary>
public class OrderLineEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Foreign Key & Navigation to OrderEntity
    public Guid OrderId { get; set; }
    public OrderEntity Order { get; set; } = null!;

    public string Sku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPriceHt { get; set; }
    public decimal LineTotalHt { get; set; }
    public double UnitWeightKg { get; set; }
}
