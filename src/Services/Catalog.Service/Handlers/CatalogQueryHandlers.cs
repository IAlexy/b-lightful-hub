namespace Catalog.Service.Handlers;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Catalog.Service.DataModels;
using Catalog.Service.Services;

public sealed class GetCatalogProductsQueryHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : IQueryHandler<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>
{
    public Task<IReadOnlyList<CatalogProductDto>> HandleAsync(GetCatalogProductsQuery query, CancellationToken ct = default)
    {
        var filter = autoMapper.Map<GetCatalogProductsQuery, CatalogProductFilterDataModel>(query);
        return catalogService.GetProductsAsync(filter, ct);
    }
}

public sealed class GetCatalogProductByIdQueryHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : IQueryHandler<GetCatalogProductByIdQuery, CatalogProductDto?>
{
    public Task<CatalogProductDto?> HandleAsync(GetCatalogProductByIdQuery query, CancellationToken ct = default)
    {
        var lookup = autoMapper.Map<GetCatalogProductByIdQuery, CatalogProductIdLookupDataModel>(query);
        return catalogService.GetProductByIdAsync(lookup.ProductId, query.IncludeArchived, ct);
    }
}

public sealed class GetCatalogProductBySkuQueryHandler(
    ICatalogService catalogService) : IQueryHandler<GetCatalogProductBySkuQuery, CatalogProductDto?>
{
    public Task<CatalogProductDto?> HandleAsync(GetCatalogProductBySkuQuery query, CancellationToken ct = default) =>
        catalogService.GetProductBySkuAsync(query.Sku, query.IncludeArchived, ct);
}

public sealed class ValidateProductAvailabilityQueryHandler(
    ICatalogService catalogService) : IQueryHandler<ValidateProductAvailabilityQuery, ProductAvailabilityCheckDto>
{
    public Task<ProductAvailabilityCheckDto> HandleAsync(ValidateProductAvailabilityQuery query, CancellationToken ct = default) =>
        catalogService.ValidateProductAvailabilityAsync(query.Sku, query.RequestedQuantity, ct);
}

public sealed class GetCatalogCategoriesQueryHandler(
    ICatalogService catalogService) : IQueryHandler<GetCatalogCategoriesQuery, IReadOnlyList<CatalogCategoryDto>>
{
    public Task<IReadOnlyList<CatalogCategoryDto>> HandleAsync(GetCatalogCategoriesQuery query, CancellationToken ct = default) =>
        catalogService.GetCategoriesAsync(query.Search, ct);
}

public sealed class GetCatalogCategoryByIdQueryHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : IQueryHandler<GetCatalogCategoryByIdQuery, CatalogCategoryDto?>
{
    public Task<CatalogCategoryDto?> HandleAsync(GetCatalogCategoryByIdQuery query, CancellationToken ct = default)
    {
        var lookup = autoMapper.Map<GetCatalogCategoryByIdQuery, CatalogCategoryIdLookupDataModel>(query);
        return catalogService.GetCategoryByIdAsync(lookup.CategoryId, ct);
    }
}

public sealed class GetProductTagsQueryHandler(
    ICatalogService catalogService) : IQueryHandler<GetProductTagsQuery, IReadOnlyList<ProductTagDto>>
{
    public Task<IReadOnlyList<ProductTagDto>> HandleAsync(GetProductTagsQuery query, CancellationToken ct = default) =>
        catalogService.GetProductTagsAsync(query.Search, ct);
}

public sealed class GetProductTagByIdQueryHandler(
    ICatalogService catalogService) : IQueryHandler<GetProductTagByIdQuery, ProductTagDto?>
{
    public Task<ProductTagDto?> HandleAsync(GetProductTagByIdQuery query, CancellationToken ct = default) =>
        catalogService.GetProductTagByIdAsync(query.TagId, ct);
}

public sealed class GetProductRefillAlertsQueryHandler(
    ICatalogService catalogService) : IQueryHandler<GetProductRefillAlertsQuery, IReadOnlyList<StockRefillAlertDto>>
{
    public Task<IReadOnlyList<StockRefillAlertDto>> HandleAsync(GetProductRefillAlertsQuery query, CancellationToken ct = default) =>
        catalogService.GetProductRefillAlertsAsync(query.ProductId, query.PendingOnly, ct);
}
