namespace Customer.Service;

/// <summary>
/// Input model for creating or updating a free custom tag on a B2B Customer.
/// </summary>
public sealed record CustomTagInput(
    string Name,
    string? Value = null,
    string? Color = "#7C3AED");

/// <summary>
/// Output DTO representing a custom tag attached to a B2B Customer.
/// </summary>
public sealed record CustomTagDto(
    Guid Id,
    string Name,
    string? Value,
    string Color);

/// <summary>
/// Output DTO representing a B2B Customer with its SIREN/SIRET, Invoicing & Delivery addresses,
/// same-address flag, and free custom tags.
/// </summary>
public sealed record B2BCustomerDto(
    Guid Id,
    string Name,
    string SirenSiret,
    string InvoicingAddress,
    string DeliveryAddress,
    bool UseInvoicingAddressForDelivery,
    string ContactEmail,
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
