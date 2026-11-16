namespace Ordering.Service;

public sealed record CustomerDto(
    Guid Id,
    string CompanyName,
    string VatNumber,
    string ContactEmail,
    string BillingAddress);

public sealed record PurchaseOrderLineDto(
    Guid Id,
    string Sku,
    string ProductName,
    int Quantity,
    decimal UnitPriceHt,
    decimal LineTotalHt,
    double UnitWeightKg);

public sealed record PurchaseOrderDto(
    Guid Id,
    string OrderNumber,
    Guid CustomerId,
    CustomerDto Customer,
    string BuyerCompanyName,
    string BuyerVatNumber,
    int PaymentTermsDays,
    string ShippingDestination,
    string Status,
    DateTime CreatedAtUtc,
    decimal TotalAmountHt,
    int TotalUnits,
    IReadOnlyList<PurchaseOrderLineDto> Lines,
    string PaymentStatus = "Unpaid",
    decimal PaidAmountHt = 0m,
    string? CancellationReason = null,
    DateTime? CancelledAtUtc = null);

public sealed record OrderSummaryDto(
    Guid Id,
    string OrderNumber,
    string BuyerCompanyName,
    string BuyerVatNumber,
    string Status,
    DateTime CreatedAtUtc,
    decimal TotalAmountHt,
    int TotalUnits,
    int LineCount,
    string PaymentStatus = "Unpaid",
    decimal PaidAmountHt = 0m);

public sealed record OrderCancellationResultDto(
    Guid OrderId,
    string OrderNumber,
    string Status,
    string PaymentStatus,
    decimal PaidAmountHt,
    decimal RemainingTotalAmountHt,
    int RemainingUnits,
    int RemainingLineCount,
    Guid? CancelledOrderLineId,
    string? CancellationReason,
    DateTime? CancelledAtUtc,
    PurchaseOrderDto Order);
