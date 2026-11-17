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
/// Placeholder DTO for the future Customer Favorite Products endpoint once Catalog persistence is implemented.
/// </summary>
public sealed record CustomerFavoriteProductDto(
    Guid CustomerId,
    string Sku,
    string ProductName,
    int OrderFrequency);
