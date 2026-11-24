namespace Catalog.Service.Entities;

/// <summary>
/// Table 1 ("CatalogCategories"): Relational B2B Product Category entity.
/// </summary>
public class CatalogCategoryEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<CatalogProductEntity> Products { get; set; } = new List<CatalogProductEntity>();
}

/// <summary>
/// Table 2 ("CatalogProducts"): Relational B2B Catalog Product entity supporting
/// soft-delete (IsArchived), stock tracking, multi-tier volume pricing, tags, and refill alerts.
/// </summary>
public class CatalogProductEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public Guid CategoryId { get; set; }
    public CatalogCategoryEntity Category { get; set; } = null!;

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

    public ICollection<ProductVolumePriceTierEntity> VolumePriceTiers { get; set; } = new List<ProductVolumePriceTierEntity>();
    public ICollection<ProductTagAssignmentEntity> TagAssignments { get; set; } = new List<ProductTagAssignmentEntity>();
    public ICollection<StockRefillAlertEntity> StockRefillAlerts { get; set; } = new List<StockRefillAlertEntity>();
}

/// <summary>
/// Table 3 ("ProductTags"): Independent Product Tag entity (matching Customer's 3-table tag pattern).
/// </summary>
public class ProductTagEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string Color { get; set; } = "#0EA5E9";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<ProductTagAssignmentEntity> ProductAssignments { get; set; } = new List<ProductTagAssignmentEntity>();
}

/// <summary>
/// Table 4 ("ProductTagAssignments"): Many-to-Many Association Entity linking
/// CatalogProducts and independent ProductTags.
/// </summary>
public class ProductTagAssignmentEntity
{
    public Guid ProductId { get; set; }
    public CatalogProductEntity Product { get; set; } = null!;

    public Guid TagId { get; set; }
    public ProductTagEntity Tag { get; set; } = null!;

    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Table 5 ("ProductVolumePriceTiers"): Multi-tier B2B volume pricing brackets per product.
/// </summary>
public class ProductVolumePriceTierEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public CatalogProductEntity Product { get; set; } = null!;

    public int MinQuantity { get; set; }
    public decimal UnitPriceHt { get; set; }
}

/// <summary>
/// Table 6 ("StockRefillAlerts"): Customer email alert subscriptions triggered when an out-of-stock
/// product transitions from 0 available stock to > 0 available stock.
/// </summary>
public class StockRefillAlertEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public CatalogProductEntity Product { get; set; } = null!;

    public Guid? CustomerId { get; set; }
    public string Email { get; set; } = string.Empty;
    public bool IsNotified { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? NotifiedAtUtc { get; set; }
}
