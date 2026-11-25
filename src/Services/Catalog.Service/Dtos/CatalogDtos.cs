namespace Catalog.Service;

/// <summary>
/// Input model for creating or referencing a product tag.
/// </summary>
public sealed record ProductTagInput(
    string Name,
    string? Value = null,
    string? Color = "#0EA5E9");

/// <summary>
/// Output DTO representing an independent Product Tag.
/// </summary>
public sealed record ProductTagDto(
    Guid Id,
    string Name,
    string? Value,
    string Color);

/// <summary>
/// Input model for defining a B2B multi-tier volume pricing bracket.
/// </summary>
public sealed record VolumePriceTierInput(
    int MinQuantity,
    decimal UnitPriceHt);

/// <summary>
/// Output DTO representing a B2B volume pricing tier.
/// </summary>
public sealed record VolumePriceTierDto(
    Guid Id,
    int MinQuantity,
    decimal UnitPriceHt,
    decimal DiscountPercent);

/// <summary>
/// Output DTO representing a relational Catalog Category.
/// </summary>
public sealed record CatalogCategoryDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    int ProductCount,
    DateTime CreatedAtUtc);

/// <summary>
/// Output DTO representing a B2B Catalog Product.
/// Preserves the original 12 positional parameters for backwards compatibility while adding
/// category ID, description, stock flags, archiving metadata, multi-tier volume pricing, and tags.
/// </summary>
public sealed record CatalogProductDto(
    Guid Id,
    string Sku,
    string Name,
    string Category,
    decimal UnitPriceHt,
    int BulkTierMinQuantity,
    decimal BulkUnitPriceHt,
    decimal BulkDiscountPercent,
    int AvailableStock,
    int ReservedStock,
    double UnitWeightKg,
    bool IsLowStock,
    Guid? CategoryId = null,
    string? Description = null,
    int LowStockThreshold = 15,
    bool IsOutOfStock = false,
    bool IsArchived = false,
    bool CanBeOrdered = true,
    IReadOnlyList<VolumePriceTierDto>? VolumePriceTiers = null,
    IReadOnlyList<ProductTagDto>? Tags = null,
    DateTime? CreatedAtUtc = null,
    DateTime? UpdatedAtUtc = null,
    DateTime? ArchivedAtUtc = null);

/// <summary>
/// Result DTO for checking whether a requested quantity of a product can be added/ordered,
/// used by both frontend validation and backend anti-bypass enforcement.
/// </summary>
public sealed record ProductAvailabilityCheckDto(
    string Sku,
    Guid? ProductId,
    bool Exists,
    bool IsArchived,
    bool IsOutOfStock,
    int AvailableStock,
    int RequestedQuantity,
    bool CanOrder,
    decimal? EffectiveUnitPriceHt,
    decimal? EstimatedLineTotalHt,
    string? ErrorCode,
    string? Message);

/// <summary>
/// Output DTO representing a back-in-stock refill email alert subscription.
/// </summary>
public sealed record StockRefillAlertDto(
    Guid Id,
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid? CustomerId,
    string Email,
    bool IsNotified,
    DateTime CreatedAtUtc,
    DateTime? NotifiedAtUtc);

/// <summary>
/// Output DTO returned by Restock / Stock Adjustment operations, reporting any back-in-stock
/// refill alert emails triggered when stock transitioned from 0 to > 0.
/// </summary>
public sealed record RestockResultDto(
    CatalogProductDto Product,
    int PreviousAvailableStock,
    int DeltaQuantity,
    int RefillAlertsTriggered,
    IReadOnlyList<string> NotifiedEmails);

/// <summary>
/// Legacy lightweight domain model preserved for IB2BMapper profile compatibility.
/// </summary>
public sealed class CatalogProduct
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Sku { get; init; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public decimal UnitPriceHt { get; set; }
    public int BulkTierMinQuantity { get; set; } = 10;
    public decimal BulkUnitPriceHt { get; set; }
    public int AvailableStock { get; set; }
    public int ReservedStock { get; set; }
    public double UnitWeightKg { get; set; } = 2.5;

    public decimal ComputeEffectiveUnitPrice(int quantity) =>
        quantity >= BulkTierMinQuantity && BulkUnitPriceHt > 0 ? BulkUnitPriceHt : UnitPriceHt;
}
