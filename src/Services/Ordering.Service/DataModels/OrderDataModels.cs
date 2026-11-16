namespace Ordering.Service.DataModels;

public enum PurchaseOrderStatus
{
    Draft,
    Confirmed,
    Cancelled
}

public enum OrderPaymentStatus
{
    Unpaid,
    PartiallyPaid,
    Paid
}

/// <summary>
/// Domain exception thrown when an order or order line cancellation violates B2B invariants
/// (e.g. order/line does not exist, order is already paid or partially paid, or already cancelled).
/// </summary>
public sealed class OrderCancellationException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Domain DataModel representing a B2B Customer in the Ordering bounded context.
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
/// Domain DataModel representing a B2B Purchase Order aggregate (Customer + Order + OrderLines)
/// and enforcing payment & cancellation invariants.
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
    public OrderPaymentStatus PaymentStatus { get; set; } = OrderPaymentStatus.Unpaid;
    public decimal PaidAmountHt { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<OrderLineDataModel> Lines { get; set; } = [];

    public decimal TotalAmountHt => Status == PurchaseOrderStatus.Cancelled
        ? 0m
        : Math.Round(Lines.Sum(l => l.LineTotalHt), 2);

    public int TotalUnits => Status == PurchaseOrderStatus.Cancelled
        ? 0
        : Lines.Sum(l => l.Quantity);

    /// <summary>
    /// Validates that the order is eligible for whole-order or line-level cancellation.
    /// Rejects orders that are already Cancelled, Paid, or PartiallyPaid.
    /// </summary>
    public void EnsureCanBeCancelled()
    {
        if (Status == PurchaseOrderStatus.Cancelled)
        {
            throw new OrderCancellationException(
                "ORDER_ALREADY_CANCELLED",
                $"Order {OrderNumber} is already cancelled.");
        }

        if (PaymentStatus == OrderPaymentStatus.Paid || (TotalAmountHt > 0 && PaidAmountHt >= TotalAmountHt))
        {
            throw new OrderCancellationException(
                "ORDER_ALREADY_PAID",
                $"Order {OrderNumber} has already been fully paid ({PaidAmountHt:0.00} EUR HT) and cannot be cancelled.");
        }

        if (PaymentStatus == OrderPaymentStatus.PartiallyPaid || PaidAmountHt > 0m)
        {
            throw new OrderCancellationException(
                "ORDER_PARTIALLY_PAID",
                $"Order {OrderNumber} has been partially paid ({PaidAmountHt:0.00} / {TotalAmountHt:0.00} EUR HT) and cannot be cancelled without a credit note.");
        }
    }

    public void ApplyCancellation(string? reason)
    {
        EnsureCanBeCancelled();
        Status = PurchaseOrderStatus.Cancelled;
        CancellationReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled by B2B customer request" : reason.Trim();
        CancelledAtUtc = DateTime.UtcNow;
    }

    public OrderLineDataModel RemoveOrderLine(Guid orderLineId, string? reason)
    {
        EnsureCanBeCancelled();

        var line = Lines.FirstOrDefault(l => l.Id == orderLineId);
        if (line is null)
        {
            throw new OrderCancellationException(
                "ORDER_LINE_NOT_FOUND",
                $"Order line {orderLineId} does not exist on order {OrderNumber}.");
        }

        Lines.Remove(line);

        // If the last remaining line of the order was removed, automatically cancel the order itself
        if (Lines.Count == 0)
        {
            Status = PurchaseOrderStatus.Cancelled;
            CancellationReason = string.IsNullOrWhiteSpace(reason)
                ? "Order automatically cancelled after all order lines were removed"
                : reason.Trim();
            CancelledAtUtc = DateTime.UtcNow;
        }

        return line;
    }
}
