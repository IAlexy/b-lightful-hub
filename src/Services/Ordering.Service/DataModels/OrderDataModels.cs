namespace Ordering.Service.DataModels;

public enum PurchaseOrderStatus
{
    Draft,
    Confirmed,
    Cancelled
}

/// <summary>
/// Domain DataModel representing a B2B Customer in the Ordering bounded context.
/// Decoupled from both the EF Core SQL Entity (<see cref="Entities.CustomerEntity"/>) and API DTOs.
/// </summary>
public class CustomerDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CompanyName { get; set; } = string.Empty;
    public string VatNumber { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string BillingAddress { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Domain DataModel representing an individual B2B Order Line with domain calculation rules.
/// </summary>
public class OrderLineDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPriceHt { get; set; }
    public double UnitWeightKg { get; set; }
    public decimal LineTotalHt => Math.Round(Quantity * UnitPriceHt, 2);
}

/// <summary>
/// Domain DataModel representing a B2B Purchase Order aggregate (Customer + Order + OrderLines).
/// </summary>
public class OrderDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNumber { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public CustomerDataModel Customer { get; set; } = new();
    public int PaymentTermsDays { get; set; } = 30;
    public string ShippingDestination { get; set; } = string.Empty;
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Confirmed;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<OrderLineDataModel> Lines { get; set; } = [];

    public decimal TotalAmountHt => Math.Round(Lines.Sum(l => l.LineTotalHt), 2);
    public int TotalUnits => Lines.Sum(l => l.Quantity);
}
