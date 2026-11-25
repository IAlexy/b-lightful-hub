namespace Catalog.Service.DataModels;

/// <summary>
/// Domain exception carrying a structured ErrorCode for Catalog CRUD, stock enforcement,
/// volume pricing, category/tag management, and back-in-stock refill alert rules.
/// </summary>
public sealed class CatalogDomainException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class CatalogCategoryDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ProductCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ProductTagDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string Color { get; set; } = "#0EA5E9";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ProductTagAssignmentDataModel
{
    public Guid ProductId { get; set; }
    public Guid TagId { get; set; }
    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class VolumePriceTierDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public int MinQuantity { get; set; }
    public decimal UnitPriceHt { get; set; }

    public decimal ComputeDiscountPercent(decimal baseUnitPriceHt) =>
        baseUnitPriceHt > 0m && UnitPriceHt > 0m && UnitPriceHt < baseUnitPriceHt
            ? Math.Round((1m - (UnitPriceHt / baseUnitPriceHt)) * 100m, 1)
            : 0m;
}

public sealed class CatalogProductDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public decimal UnitPriceHt { get; set; }
    public int BulkTierMinQuantity { get; set; } = 10;
    public decimal BulkUnitPriceHt { get; set; }
    public int AvailableStock { get; set; }
    public int ReservedStock { get; set; }
    public int LowStockThreshold { get; set; } = 15;
    public double UnitWeightKg { get; set; } = 2.5;
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<VolumePriceTierDataModel> VolumePriceTiers { get; set; } = [];
    public List<Guid> TagIds { get; set; } = [];
    public List<ProductTagDataModel> Tags { get; set; } = [];

    public bool IsOutOfStock => AvailableStock <= 0;
    public bool IsLowStock => AvailableStock < LowStockThreshold;
    public bool CanBeOrdered => !IsArchived && AvailableStock > 0;

    /// <summary>
    /// Computes the effective B2B unit price HT for a given quantity using multi-tier volume brackets
    /// (falls back to BulkTierMinQuantity/BulkUnitPriceHt and then base UnitPriceHt).
    /// </summary>
    public decimal ComputeEffectiveUnitPrice(int quantity)
    {
        if (VolumePriceTiers.Count > 0)
        {
            var matchedTier = VolumePriceTiers
                .Where(t => quantity >= t.MinQuantity && t.UnitPriceHt > 0m)
                .OrderByDescending(t => t.MinQuantity)
                .FirstOrDefault();

            if (matchedTier is not null)
            {
                return matchedTier.UnitPriceHt;
            }
        }

        return quantity >= BulkTierMinQuantity && BulkUnitPriceHt > 0m
            ? BulkUnitPriceHt
            : UnitPriceHt;
    }

    public decimal ComputeBulkDiscountPercent()
    {
        if (UnitPriceHt <= 0m)
        {
            return 0m;
        }

        if (VolumePriceTiers.Count > 0)
        {
            var primaryTier = VolumePriceTiers
                .OrderBy(t => t.MinQuantity)
                .First();
            return primaryTier.ComputeDiscountPercent(UnitPriceHt);
        }

        return BulkUnitPriceHt > 0m && BulkUnitPriceHt < UnitPriceHt
            ? Math.Round((1m - (BulkUnitPriceHt / UnitPriceHt)) * 100m, 1)
            : 0m;
    }
}

public sealed class StockRefillAlertDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public string Email { get; set; } = string.Empty;
    public bool IsNotified { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? NotifiedAtUtc { get; set; }
}

public sealed class CatalogProductFilterDataModel
{
    public string? Category { get; set; }
    public string? Search { get; set; }
    public string? Tag { get; set; }
    public bool? LowStockOnly { get; set; }
    public bool? InStockOnly { get; set; }
    public bool IncludeArchived { get; set; }
}

public sealed class CatalogProductIdLookupDataModel
{
    public Guid ProductId { get; set; }
}

public sealed class CatalogCategoryIdLookupDataModel
{
    public Guid CategoryId { get; set; }
}
