namespace Catalog.Service;

using B2B.BuildingBlocks.Cqrs;

// ==========================================
// 1. PRODUCT CRUD & ARCHIVING COMMANDS
// ==========================================

public sealed record CreateCatalogProductCommand(
    string Sku,
    string Name,
    string Category,
    decimal UnitPriceHt,
    int BulkTierMinQuantity,
    decimal BulkUnitPriceHt,
    int InitialStock,
    double UnitWeightKg,
    string? Description = null,
    Guid? CategoryId = null,
    int LowStockThreshold = 15,
    IReadOnlyList<VolumePriceTierInput>? VolumePriceTiers = null,
    IReadOnlyList<Guid>? TagIds = null,
    IReadOnlyList<ProductTagInput>? Tags = null) : ICommand<CatalogProductDto>;

public sealed record UpdateCatalogProductCommand(
    Guid ProductId,
    string Name,
    string Category,
    decimal UnitPriceHt,
    int BulkTierMinQuantity,
    decimal BulkUnitPriceHt,
    double UnitWeightKg,
    string? Description = null,
    Guid? CategoryId = null,
    int LowStockThreshold = 15,
    IReadOnlyList<VolumePriceTierInput>? VolumePriceTiers = null,
    IReadOnlyList<Guid>? TagIds = null,
    IReadOnlyList<ProductTagInput>? Tags = null) : ICommand<CatalogProductDto>;

public sealed record ArchiveCatalogProductCommand(Guid ProductId) : ICommand<CatalogProductDto>;

public sealed record UnarchiveCatalogProductCommand(Guid ProductId) : ICommand<CatalogProductDto>;

// ==========================================
// 2. RELATIONAL CATEGORY COMMANDS
// ==========================================

public sealed record CreateCatalogCategoryCommand(
    string Name,
    string? Description = null) : ICommand<CatalogCategoryDto>;

public sealed record UpdateCatalogCategoryCommand(
    Guid CategoryId,
    string Name,
    string? Description = null) : ICommand<CatalogCategoryDto>;

public sealed record DeleteCatalogCategoryCommand(Guid CategoryId) : ICommand<bool>;

// ==========================================
// 3. INDEPENDENT PRODUCT TAGS & ASSIGNMENTS (3-TABLE PATTERN)
// ==========================================

public sealed record CreateProductTagCommand(
    string Name,
    string? Value = null,
    string? Color = "#0EA5E9") : ICommand<ProductTagDto>;

public sealed record DeleteProductTagCommand(Guid TagId) : ICommand<bool>;

public sealed record AssignTagToProductCommand(
    Guid ProductId,
    Guid TagId) : ICommand<CatalogProductDto>;

public sealed record RemoveTagFromProductCommand(
    Guid ProductId,
    Guid TagId) : ICommand<CatalogProductDto>;

// ==========================================
// 4. MULTI-TIER B2B VOLUME PRICING COMMANDS
// ==========================================

public sealed record SetProductVolumePriceTiersCommand(
    Guid ProductId,
    IReadOnlyList<VolumePriceTierInput> Tiers) : ICommand<CatalogProductDto>;

// ==========================================
// 5. STOCK OPERATIONS & BACK-IN-STOCK REFILL ALERTS
// ==========================================

public sealed record ReserveCatalogStockCommand(
    string Sku,
    int Quantity) : ICommand<CatalogProductDto?>;

public sealed record ReleaseCatalogStockCommand(
    string Sku,
    int Quantity) : ICommand<CatalogProductDto?>;

public sealed record RestockCatalogProductCommand(
    Guid ProductId,
    int QuantityAdded) : ICommand<RestockResultDto>;

public sealed record AdjustCatalogStockCommand(
    Guid ProductId,
    int NewAvailableStock,
    string? Reason = null) : ICommand<RestockResultDto>;

public sealed record SubscribeStockRefillAlertCommand(
    Guid ProductId,
    string Email,
    Guid? CustomerId = null) : ICommand<StockRefillAlertDto>;

public sealed record UnsubscribeStockRefillAlertCommand(Guid AlertId) : ICommand<bool>;
