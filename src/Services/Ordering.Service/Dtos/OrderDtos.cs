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
    IReadOnlyList<PurchaseOrderLineDto> Lines);

public sealed record OrderSummaryDto(
    Guid Id,
    string OrderNumber,
    string BuyerCompanyName,
    string BuyerVatNumber,
    string Status,
    DateTime CreatedAtUtc,
    decimal TotalAmountHt,
    int TotalUnits,
    int LineCount);
