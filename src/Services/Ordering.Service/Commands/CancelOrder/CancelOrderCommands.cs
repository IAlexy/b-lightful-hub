namespace Ordering.Service;

using B2B.BuildingBlocks.Cqrs;

public sealed class CancelOrderDataModel
{
    public Guid OrderId { get; set; }
    public string Reason { get; set; } = "Cancelled by B2B customer request";
}

public sealed class CancelOrderLineDataModel
{
    public Guid OrderId { get; set; }
    public Guid OrderLineId { get; set; }
    public string Reason { get; set; } = "Order line removed by B2B customer";
}

/// <summary>
/// Pure Command data model to cancel an entire B2B Purchase Order.
/// </summary>
public sealed record CancelPurchaseOrderCommand(
    Guid OrderId,
    string? Reason = null) : ICommand<OrderCancellationResultDto>;

/// <summary>
/// Pure Command data model to cancel/remove a specific Order Line from a B2B Purchase Order.
/// </summary>
public sealed record CancelOrderLineCommand(
    Guid OrderId,
    Guid OrderLineId,
    string? Reason = null) : ICommand<OrderCancellationResultDto>;

/// <summary>
/// Pure Command data model to record a partial or full payment against an Order.
/// </summary>
public sealed record RecordOrderPaymentCommand(
    Guid OrderId,
    decimal AmountPaidHt) : ICommand<PurchaseOrderDto>;
