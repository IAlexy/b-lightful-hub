namespace Ordering.Service;

using B2B.BuildingBlocks.Cqrs;

/// <summary>
/// Pure Query data model to retrieve the list of B2B Purchase Orders.
/// </summary>
public sealed record GetPurchaseOrdersQuery(string? BuyerVatNumber = null) : IQuery<IReadOnlyList<PurchaseOrderDto>>;

/// <summary>
/// Pure Query data model to retrieve a specific B2B Purchase Order by ID.
/// </summary>
public sealed record GetPurchaseOrderByIdQuery(Guid OrderId) : IQuery<PurchaseOrderDto?>;
