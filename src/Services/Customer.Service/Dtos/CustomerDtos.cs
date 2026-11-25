namespace Customer.Service;

/// <summary>
/// Input model for creating or referencing a custom tag.
/// </summary>
public sealed record CustomTagInput(
    string Name,
    string? Value = null,
    string? Color = "#7C3AED");

/// <summary>
/// Output DTO representing an independent Custom Tag.
/// </summary>
public sealed record CustomTagDto(
    Guid Id,
    string Name,
    string? Value,
    string Color);

/// <summary>
/// Output DTO representing a B2B Customer with its SIREN/SIRET, Invoicing & Delivery addresses,
/// same-address flag, ContactEmail, PhoneNumber, and associated Custom Tags.
/// </summary>
public sealed record B2BCustomerDto(
    Guid Id,
    string Name,
    string SirenSiret,
    string InvoicingAddress,
    string DeliveryAddress,
    bool UseInvoicingAddressForDelivery,
    string? ContactEmail,
    string? PhoneNumber,
    IReadOnlyList<CustomTagDto> Tags,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// Output DTO representing a B2B Customer's Favorite Product (combining automatic order frequency
/// with manual favorite pinning, enriched with live Catalog.Service pricing and stock data).
/// </summary>
public sealed record CustomerFavoriteProductDto(
    Guid CustomerId,
    Guid? ProductId,
    string Sku,
    string ProductName,
    string? Category,
    decimal UnitPriceHt,
    int AvailableStock,
    bool IsOutOfStock,
    bool IsArchived,
    int OrderFrequency,
    int TotalUnitsOrdered,
    bool IsManualFavorite,
    DateTime? LastOrderedAtUtc = null);

/// <summary>
/// Output DTO representing a curated Wishlist item saved by a B2B Customer (separate from frequently ordered favorites).
/// </summary>
public sealed record CustomerWishlistItemDto(
    Guid Id,
    Guid CustomerId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string Category,
    decimal UnitPriceHt,
    int AvailableStock,
    bool IsOutOfStock,
    bool IsArchived,
    string? Note,
    int Priority,
    DateTime AddedAtUtc);
