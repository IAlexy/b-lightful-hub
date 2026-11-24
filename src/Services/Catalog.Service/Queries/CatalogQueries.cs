namespace Catalog.Service;

using B2B.BuildingBlocks.Cqrs;

public sealed record GetCatalogProductsQuery(
    string? Category = null,
    string? Search = null,
    string? Tag = null,
    bool? LowStockOnly = null,
    bool? InStockOnly = null,
    bool IncludeArchived = false) : IQuery<IReadOnlyList<CatalogProductDto>>;

public sealed record GetCatalogProductByIdQuery(
    Guid ProductId,
    bool IncludeArchived = true) : IQuery<CatalogProductDto?>;

public sealed record GetCatalogProductBySkuQuery(
    string Sku,
    bool IncludeArchived = false) : IQuery<CatalogProductDto?>;

public sealed record ValidateProductAvailabilityQuery(
    string Sku,
    int RequestedQuantity) : IQuery<ProductAvailabilityCheckDto>;

public sealed record GetCatalogCategoriesQuery(string? Search = null) : IQuery<IReadOnlyList<CatalogCategoryDto>>;

public sealed record GetCatalogCategoryByIdQuery(Guid CategoryId) : IQuery<CatalogCategoryDto?>;

public sealed record GetProductTagsQuery(string? Search = null) : IQuery<IReadOnlyList<ProductTagDto>>;

public sealed record GetProductTagByIdQuery(Guid TagId) : IQuery<ProductTagDto?>;

public sealed record GetProductRefillAlertsQuery(
    Guid ProductId,
    bool PendingOnly = true) : IQuery<IReadOnlyList<StockRefillAlertDto>>;
