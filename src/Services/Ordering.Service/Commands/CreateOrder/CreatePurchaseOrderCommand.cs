namespace Ordering.Service;

using B2B.BuildingBlocks.Cqrs;

public sealed record OrderLineRequest(string Sku, int Quantity);

/// <summary>
/// Pure CQRS Command data model to create a B2B Customer (if not yet registered by VAT number),
/// a Purchase Order, and its Order Lines in the SQL database via EF Core.
/// </summary>
public sealed record CreatePurchaseOrderCommand(
    string BuyerCompanyName,
    string BuyerVatNumber,
    int PaymentTermsDays,
    string ShippingDestination,
    IReadOnlyList<OrderLineRequest> Items,
    bool AutoConfirm = true,
    string? ContactEmail = null) : ICommand<PurchaseOrderDto>;
