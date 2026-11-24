namespace Customer.Service;

using B2B.BuildingBlocks.Cqrs;
using Delivery.Service;
using Invoicing.Service;
using Ordering.Service;

/// <summary>
/// Pure Query data model to list B2B Customers with optional text search or custom tag filter.
/// </summary>
public sealed record GetB2BCustomersQuery(
    string? Search = null,
    string? Tag = null) : IQuery<IReadOnlyList<B2BCustomerDto>>;

/// <summary>
/// Pure Query data model to retrieve a specific B2B Customer by ID.
/// </summary>
public sealed record GetB2BCustomerByIdQuery(Guid CustomerId) : IQuery<B2BCustomerDto?>;

/// <summary>
/// Pure Query data model to list all independent Custom Tags.
/// </summary>
public sealed record GetCustomTagsQuery(string? Search = null) : IQuery<IReadOnlyList<CustomTagDto>>;

/// <summary>
/// Pure Query data model to retrieve a specific independent Custom Tag by ID.
/// </summary>
public sealed record GetCustomTagByIdQuery(Guid TagId) : IQuery<CustomTagDto?>;

/// <summary>
/// Pure Query data model to retrieve all Purchase Orders belonging to a B2B Customer.
/// </summary>
public sealed record GetCustomerOrdersQuery(Guid CustomerId) : IQuery<IReadOnlyList<PurchaseOrderDto>>;

/// <summary>
/// Pure Query data model to retrieve all Invoices belonging to a B2B Customer.
/// </summary>
public sealed record GetCustomerInvoicesQuery(Guid CustomerId) : IQuery<IReadOnlyList<B2BInvoiceDto>>;

/// <summary>
/// Pure Query data model to retrieve all Deliveries (Shipments) belonging to a B2B Customer.
/// </summary>
public sealed record GetCustomerDeliveriesQuery(Guid CustomerId) : IQuery<IReadOnlyList<B2BShipmentDto>>;

/// <summary>
/// Pure Query data model to retrieve all Favorite Products (auto-ordered frequently + manual favorites) for a B2B Customer.
/// </summary>
public sealed record GetCustomerFavoriteProductsQuery(Guid CustomerId) : IQuery<IReadOnlyList<CustomerFavoriteProductDto>>;

/// <summary>
/// Pure Query data model to retrieve all Wishlist items saved by a B2B Customer.
/// </summary>
public sealed record GetCustomerWishlistQuery(Guid CustomerId) : IQuery<IReadOnlyList<CustomerWishlistItemDto>>;
