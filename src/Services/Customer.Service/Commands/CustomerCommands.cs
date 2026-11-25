namespace Customer.Service;

using B2B.BuildingBlocks.Cqrs;

/// <summary>
/// Pure Command data model to create a new B2B Customer.
/// Business rules (required fields, at least ContactEmail or PhoneNumber, same-address copy)
/// are enforced in CustomerCrudService.
/// </summary>
public sealed record CreateB2BCustomerCommand(
    string Name,
    string SirenSiret,
    string InvoicingAddress,
    string? DeliveryAddress = null,
    bool UseInvoicingAddressForDelivery = false,
    string? ContactEmail = null,
    string? PhoneNumber = null,
    IReadOnlyList<Guid>? TagIds = null,
    IReadOnlyList<CustomTagInput>? Tags = null) : ICommand<B2BCustomerDto>;

/// <summary>
/// Pure Command data model to update an existing B2B Customer.
/// </summary>
public sealed record UpdateB2BCustomerCommand(
    Guid CustomerId,
    string Name,
    string SirenSiret,
    string InvoicingAddress,
    string? DeliveryAddress = null,
    bool UseInvoicingAddressForDelivery = false,
    string? ContactEmail = null,
    string? PhoneNumber = null,
    IReadOnlyList<Guid>? TagIds = null,
    IReadOnlyList<CustomTagInput>? Tags = null) : ICommand<B2BCustomerDto>;

/// <summary>
/// Pure Command data model to delete an existing B2B Customer.
/// </summary>
public sealed record DeleteB2BCustomerCommand(Guid CustomerId) : ICommand<bool>;

/// <summary>
/// Pure Command data model to create an independent Custom Tag (not tied to any specific customer).
/// </summary>
public sealed record CreateCustomTagCommand(
    string Name,
    string? Value = null,
    string? Color = "#7C3AED") : ICommand<CustomTagDto>;

/// <summary>
/// Pure Command data model to delete an independent Custom Tag from the catalog.
/// </summary>
public sealed record DeleteCustomTagCommand(Guid TagId) : ICommand<bool>;

/// <summary>
/// Pure Command data model to associate an existing independent Custom Tag with a Customer
/// in the 3rd association table ("CustomerTagAssignments").
/// </summary>
public sealed record AssignTagToCustomerCommand(
    Guid CustomerId,
    Guid TagId) : ICommand<B2BCustomerDto>;

/// <summary>
/// Pure Command data model to remove a Custom Tag association from a Customer
/// in the 3rd association table ("CustomerTagAssignments") without deleting the tag itself.
/// </summary>
public sealed record RemoveTagFromCustomerCommand(
    Guid CustomerId,
    Guid TagId) : ICommand<B2BCustomerDto>;

/// <summary>
/// Pure Command data model to manually pin a Catalog Product as a Customer's Favorite Product.
/// </summary>
public sealed record AddManualFavoriteProductCommand(
    Guid CustomerId,
    Guid ProductId) : ICommand<CustomerFavoriteProductDto>;

/// <summary>
/// Pure Command data model to remove a manually pinned Favorite Product for a Customer.
/// </summary>
public sealed record RemoveManualFavoriteProductCommand(
    Guid CustomerId,
    Guid ProductId) : ICommand<bool>;

/// <summary>
/// Pure Command data model to add a Catalog Product to a Customer's personal Wishlist.
/// </summary>
public sealed record AddProductToWishlistCommand(
    Guid CustomerId,
    Guid ProductId,
    string? Note = null,
    int Priority = 3) : ICommand<CustomerWishlistItemDto>;

/// <summary>
/// Pure Command data model to update note/priority of a Wishlist item.
/// </summary>
public sealed record UpdateWishlistItemCommand(
    Guid CustomerId,
    Guid ProductId,
    string? Note,
    int Priority) : ICommand<CustomerWishlistItemDto>;

/// <summary>
/// Pure Command data model to remove a Catalog Product from a Customer's Wishlist.
/// </summary>
public sealed record RemoveProductFromWishlistCommand(
    Guid CustomerId,
    Guid ProductId) : ICommand<bool>;
