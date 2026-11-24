namespace Catalog.Service.Services;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AutoMapper;
using B2B.BuildingBlocks.Notifications;
using Catalog.Service.DataModels;
using Catalog.Service.Entities;
using Catalog.Service.Persistence;
using Microsoft.EntityFrameworkCore;

public interface ICatalogService
{
    // Products CRUD & Soft-Delete Archiving
    Task<CatalogProductDto> CreateProductAsync(CatalogProductDataModel model, CancellationToken ct = default);
    Task<CatalogProductDto> UpdateProductAsync(CatalogProductDataModel model, CancellationToken ct = default);
    Task<CatalogProductDto> ArchiveProductAsync(Guid productId, CancellationToken ct = default);
    Task<CatalogProductDto> UnarchiveProductAsync(Guid productId, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogProductDto>> GetProductsAsync(CatalogProductFilterDataModel filter, CancellationToken ct = default);
    Task<CatalogProductDto?> GetProductByIdAsync(Guid productId, bool includeArchived = true, CancellationToken ct = default);
    Task<CatalogProductDto?> GetProductBySkuAsync(string sku, bool includeArchived = false, CancellationToken ct = default);

    // Pricing & Availability Validation (Frontend + Backend anti-bypass source of truth)
    Task<ProductAvailabilityCheckDto> ValidateProductAvailabilityAsync(string sku, int requestedQuantity, CancellationToken ct = default);
    Task<decimal?> ComputeEffectiveUnitPriceAsync(string sku, int quantity, CancellationToken ct = default);

    // Categories CRUD
    Task<CatalogCategoryDto> CreateCategoryAsync(CatalogCategoryDataModel model, CancellationToken ct = default);
    Task<CatalogCategoryDto> UpdateCategoryAsync(CatalogCategoryDataModel model, CancellationToken ct = default);
    Task<bool> DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogCategoryDto>> GetCategoriesAsync(string? search, CancellationToken ct = default);
    Task<CatalogCategoryDto?> GetCategoryByIdAsync(Guid categoryId, CancellationToken ct = default);

    // Independent Product Tags & Assignments (3-table pattern)
    Task<ProductTagDto> CreateProductTagAsync(ProductTagDataModel model, CancellationToken ct = default);
    Task<bool> DeleteProductTagAsync(Guid tagId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductTagDto>> GetProductTagsAsync(string? search, CancellationToken ct = default);
    Task<ProductTagDto?> GetProductTagByIdAsync(Guid tagId, CancellationToken ct = default);
    Task<CatalogProductDto> AssignTagToProductAsync(Guid productId, Guid tagId, CancellationToken ct = default);
    Task<CatalogProductDto> RemoveTagFromProductAsync(Guid productId, Guid tagId, CancellationToken ct = default);

    // Multi-tier Volume Pricing
    Task<CatalogProductDto> SetVolumePriceTiersAsync(Guid productId, IReadOnlyList<VolumePriceTierDataModel> tiers, CancellationToken ct = default);

    // Stock Operations & Back-in-Stock Refill Alerts
    Task<CatalogProductDto?> ReserveStockAsync(string sku, int quantity, CancellationToken ct = default);
    Task<CatalogProductDto?> ReleaseStockAsync(string sku, int quantity, CancellationToken ct = default);
    Task<RestockResultDto> RestockProductAsync(Guid productId, int quantityAdded, CancellationToken ct = default);
    Task<RestockResultDto> AdjustStockAsync(Guid productId, int newAvailableStock, string? reason = null, CancellationToken ct = default);
    Task<StockRefillAlertDto> SubscribeStockRefillAlertAsync(Guid productId, string email, Guid? customerId = null, CancellationToken ct = default);
    Task<bool> UnsubscribeStockRefillAlertAsync(Guid alertId, CancellationToken ct = default);
    Task<IReadOnlyList<StockRefillAlertDto>> GetProductRefillAlertsAsync(Guid productId, bool pendingOnly = true, CancellationToken ct = default);
}

public sealed class CatalogCrudService(
    CatalogDbContext dbContext,
    IMapper autoMapper,
    IEmailNotificationService emailNotificationService) : ICatalogService
{
    private static readonly Regex BasicEmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // =========================================================================
    // 1. PRODUCT CRUD & SOFT-DELETE ARCHIVING
    // =========================================================================

    public async Task<CatalogProductDto> CreateProductAsync(CatalogProductDataModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateAndNormalizeProductModel(model, isCreate: true);

        var normalizedSku = model.Sku;
        var skuExists = await dbContext.Products
            .AnyAsync(p => p.Sku.ToLower() == normalizedSku.ToLower(), ct);

        if (skuExists)
        {
            throw new CatalogDomainException(
                "DUPLICATE_SKU",
                $"A catalog product with SKU '{normalizedSku}' already exists.");
        }

        var categoryEntity = await ResolveOrCreateCategoryAsync(model.CategoryId, model.CategoryName, ct);
        var resolvedTiers = NormalizeAndValidateTiers(model);
        var resolvedTags = await ResolveIndependentTagsForProductAsync(model, ct);

        var now = DateTime.UtcNow;
        var entity = autoMapper.Map<CatalogProductDataModel, CatalogProductEntity>(model);
        entity.CategoryId = categoryEntity.Id;
        entity.Category = categoryEntity;
        entity.IsArchived = false;
        entity.ArchivedAtUtc = null;
        entity.CreatedAtUtc = now;
        entity.UpdatedAtUtc = now;

        SyncPrimaryBulkFieldsFromTiers(entity, resolvedTiers);

        foreach (var tier in resolvedTiers)
        {
            var tierEntity = autoMapper.Map<VolumePriceTierDataModel, ProductVolumePriceTierEntity>(tier);
            tierEntity.ProductId = entity.Id;
            entity.VolumePriceTiers.Add(tierEntity);
        }

        foreach (var tagEntity in resolvedTags)
        {
            entity.TagAssignments.Add(new ProductTagAssignmentEntity
            {
                ProductId = entity.Id,
                TagId = tagEntity.Id,
                Tag = tagEntity,
                AssignedAtUtc = now
            });
        }

        dbContext.Products.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        return MapProductEntityToDto(entity);
    }

    public async Task<CatalogProductDto> UpdateProductAsync(CatalogProductDataModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateAndNormalizeProductModel(model, isCreate: false);

        var entity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Id == model.Id, ct);

        if (entity is null)
        {
            throw new CatalogDomainException(
                "PRODUCT_NOT_FOUND",
                $"Catalog product {model.Id} was not found.");
        }

        var categoryEntity = await ResolveOrCreateCategoryAsync(model.CategoryId, model.CategoryName, ct);
        model.Sku = entity.Sku;
        var resolvedTiers = NormalizeAndValidateTiers(model);
        var resolvedTags = await ResolveIndependentTagsForProductAsync(model, ct);

        entity.Name = model.Name;
        entity.Description = model.Description;
        entity.CategoryId = categoryEntity.Id;
        entity.Category = categoryEntity;
        entity.UnitPriceHt = model.UnitPriceHt;
        entity.BulkTierMinQuantity = model.BulkTierMinQuantity;
        entity.BulkUnitPriceHt = model.BulkUnitPriceHt;
        entity.LowStockThreshold = model.LowStockThreshold;
        entity.UnitWeightKg = model.UnitWeightKg;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        ReplaceVolumePriceTiers(entity, resolvedTiers);

        if (model.TagIds.Count > 0 || model.Tags.Count > 0)
        {
            ReplaceTagAssignments(entity, resolvedTags);
        }

        await dbContext.SaveChangesAsync(ct);

        return MapProductEntityToDto(entity);
    }

    public async Task<CatalogProductDto> ArchiveProductAsync(Guid productId, CancellationToken ct = default)
    {
        var entity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (entity is null)
        {
            throw new CatalogDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        if (entity.IsArchived)
        {
            throw new CatalogDomainException(
                "PRODUCT_ARCHIVED",
                $"Catalog product '{entity.Sku}' is already archived.");
        }

        var now = DateTime.UtcNow;
        entity.IsArchived = true;
        entity.ArchivedAtUtc = now;
        entity.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(ct);

        return MapProductEntityToDto(entity);
    }

    public async Task<CatalogProductDto> UnarchiveProductAsync(Guid productId, CancellationToken ct = default)
    {
        var entity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (entity is null)
        {
            throw new CatalogDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        if (!entity.IsArchived)
        {
            throw new CatalogDomainException(
                "PRODUCT_NOT_ARCHIVED",
                $"Catalog product '{entity.Sku}' is not archived.");
        }

        entity.IsArchived = false;
        entity.ArchivedAtUtc = null;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        return MapProductEntityToDto(entity);
    }

    public async Task<IReadOnlyList<CatalogProductDto>> GetProductsAsync(
        CatalogProductFilterDataModel filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        IQueryable<CatalogProductEntity> query = QueryProductsWithIncludes().AsNoTracking();

        if (!filter.IncludeArchived)
        {
            query = query.Where(p => !p.IsArchived);
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var cat = filter.Category.Trim().ToLowerInvariant();
            query = query.Where(p =>
                p.Category.Name.ToLower() == cat ||
                p.Category.Slug.ToLower() == cat);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(p =>
                p.Sku.ToLower().Contains(term) ||
                p.Name.ToLower().Contains(term) ||
                (p.Description != null && p.Description.ToLower().Contains(term)) ||
                p.Category.Name.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            var tagTerm = filter.Tag.Trim().ToLowerInvariant();
            query = query.Where(p => p.TagAssignments.Any(a => a.Tag.Name.ToLower() == tagTerm));
        }

        if (filter.LowStockOnly == true)
        {
            query = query.Where(p => p.AvailableStock < p.LowStockThreshold);
        }

        if (filter.InStockOnly == true)
        {
            query = query.Where(p => p.AvailableStock > 0);
        }

        var entities = await query
            .OrderBy(p => p.Sku)
            .ToListAsync(ct);

        return entities.Select(MapProductEntityToDto).ToList();
    }

    public async Task<CatalogProductDto?> GetProductByIdAsync(
        Guid productId,
        bool includeArchived = true,
        CancellationToken ct = default)
    {
        var query = QueryProductsWithIncludes().AsNoTracking().Where(p => p.Id == productId);
        if (!includeArchived)
        {
            query = query.Where(p => !p.IsArchived);
        }

        var entity = await query.FirstOrDefaultAsync(ct);
        return entity is null ? null : MapProductEntityToDto(entity);
    }

    public async Task<CatalogProductDto?> GetProductBySkuAsync(
        string sku,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return null;
        }

        var normalized = sku.Trim().ToUpperInvariant();
        var query = QueryProductsWithIncludes()
            .AsNoTracking()
            .Where(p => p.Sku.ToLower() == normalized.ToLower());

        if (!includeArchived)
        {
            query = query.Where(p => !p.IsArchived);
        }

        var entity = await query.FirstOrDefaultAsync(ct);
        return entity is null ? null : MapProductEntityToDto(entity);
    }

    // =========================================================================
    // 2. AVAILABILITY & MULTI-TIER PRICING VALIDATION (FRONTEND + BACKEND)
    // =========================================================================

    public async Task<ProductAvailabilityCheckDto> ValidateProductAvailabilityAsync(
        string sku,
        int requestedQuantity,
        CancellationToken ct = default)
    {
        var normalizedSku = sku?.Trim().ToUpperInvariant() ?? string.Empty;
        if (requestedQuantity <= 0)
        {
            return new ProductAvailabilityCheckDto(
                Sku: normalizedSku,
                ProductId: null,
                Exists: false,
                IsArchived: false,
                IsOutOfStock: false,
                AvailableStock: 0,
                RequestedQuantity: requestedQuantity,
                CanOrder: false,
                EffectiveUnitPriceHt: null,
                EstimatedLineTotalHt: null,
                ErrorCode: "INVALID_QUANTITY",
                Message: "Requested quantity must be strictly positive.");
        }

        var entity = await QueryProductsWithIncludes()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Sku.ToLower() == normalizedSku.ToLower(), ct);

        if (entity is null)
        {
            return new ProductAvailabilityCheckDto(
                Sku: normalizedSku,
                ProductId: null,
                Exists: false,
                IsArchived: false,
                IsOutOfStock: false,
                AvailableStock: 0,
                RequestedQuantity: requestedQuantity,
                CanOrder: false,
                EffectiveUnitPriceHt: null,
                EstimatedLineTotalHt: null,
                ErrorCode: "PRODUCT_NOT_FOUND",
                Message: $"Catalog product '{normalizedSku}' was not found.");
        }

        var dataModel = autoMapper.Map<CatalogProductEntity, CatalogProductDataModel>(entity);

        if (dataModel.IsArchived)
        {
            return new ProductAvailabilityCheckDto(
                Sku: dataModel.Sku,
                ProductId: dataModel.Id,
                Exists: true,
                IsArchived: true,
                IsOutOfStock: dataModel.IsOutOfStock,
                AvailableStock: dataModel.AvailableStock,
                RequestedQuantity: requestedQuantity,
                CanOrder: false,
                EffectiveUnitPriceHt: null,
                EstimatedLineTotalHt: null,
                ErrorCode: "PRODUCT_ARCHIVED",
                Message: $"Product '{dataModel.Sku}' is archived and cannot be added or ordered.");
        }

        if (dataModel.AvailableStock <= 0)
        {
            return new ProductAvailabilityCheckDto(
                Sku: dataModel.Sku,
                ProductId: dataModel.Id,
                Exists: true,
                IsArchived: false,
                IsOutOfStock: true,
                AvailableStock: 0,
                RequestedQuantity: requestedQuantity,
                CanOrder: false,
                EffectiveUnitPriceHt: null,
                EstimatedLineTotalHt: null,
                ErrorCode: "OUT_OF_STOCK",
                Message: $"Product '{dataModel.Sku}' is currently out of stock and cannot be added or ordered.");
        }

        if (requestedQuantity > dataModel.AvailableStock)
        {
            return new ProductAvailabilityCheckDto(
                Sku: dataModel.Sku,
                ProductId: dataModel.Id,
                Exists: true,
                IsArchived: false,
                IsOutOfStock: false,
                AvailableStock: dataModel.AvailableStock,
                RequestedQuantity: requestedQuantity,
                CanOrder: false,
                EffectiveUnitPriceHt: null,
                EstimatedLineTotalHt: null,
                ErrorCode: "INSUFFICIENT_STOCK",
                Message: $"Requested quantity ({requestedQuantity}) for '{dataModel.Sku}' exceeds available stock ({dataModel.AvailableStock}).");
        }

        var effectiveUnitPrice = dataModel.ComputeEffectiveUnitPrice(requestedQuantity);
        return new ProductAvailabilityCheckDto(
            Sku: dataModel.Sku,
            ProductId: dataModel.Id,
            Exists: true,
            IsArchived: false,
            IsOutOfStock: false,
            AvailableStock: dataModel.AvailableStock,
            RequestedQuantity: requestedQuantity,
            CanOrder: true,
            EffectiveUnitPriceHt: effectiveUnitPrice,
            EstimatedLineTotalHt: Math.Round(effectiveUnitPrice * requestedQuantity, 2),
            ErrorCode: null,
            Message: "Product is available for ordering.");
    }

    public async Task<decimal?> ComputeEffectiveUnitPriceAsync(string sku, int quantity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sku) || quantity <= 0)
        {
            return null;
        }

        var normalized = sku.Trim().ToUpperInvariant();
        var entity = await dbContext.Products
            .AsNoTracking()
            .Include(p => p.VolumePriceTiers)
            .FirstOrDefaultAsync(p => p.Sku.ToLower() == normalized.ToLower(), ct);

        if (entity is null)
        {
            return null;
        }

        var dataModel = autoMapper.Map<CatalogProductEntity, CatalogProductDataModel>(entity);
        return dataModel.ComputeEffectiveUnitPrice(quantity);
    }

    // =========================================================================
    // 3. RELATIONAL CATEGORIES CRUD
    // =========================================================================

    public async Task<CatalogCategoryDto> CreateCategoryAsync(CatalogCategoryDataModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateAndNormalizeCategoryModel(model);

        var duplicate = await dbContext.Categories
            .AnyAsync(c => c.Name.ToLower() == model.Name.ToLower() || c.Slug == model.Slug, ct);

        if (duplicate)
        {
            throw new CatalogDomainException(
                "DUPLICATE_CATEGORY_NAME",
                $"A catalog category named '{model.Name}' already exists.");
        }

        var entity = autoMapper.Map<CatalogCategoryDataModel, CatalogCategoryEntity>(model);
        entity.CreatedAtUtc = DateTime.UtcNow;

        dbContext.Categories.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        var savedModel = autoMapper.Map<CatalogCategoryEntity, CatalogCategoryDataModel>(entity);
        return autoMapper.Map<CatalogCategoryDataModel, CatalogCategoryDto>(savedModel);
    }

    public async Task<CatalogCategoryDto> UpdateCategoryAsync(CatalogCategoryDataModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateAndNormalizeCategoryModel(model);

        var entity = await dbContext.Categories
            .Include(c => c.Products)
            .FirstOrDefaultAsync(c => c.Id == model.Id, ct);

        if (entity is null)
        {
            throw new CatalogDomainException("CATEGORY_NOT_FOUND", $"Catalog category {model.Id} was not found.");
        }

        var duplicate = await dbContext.Categories
            .AnyAsync(c => c.Id != model.Id && (c.Name.ToLower() == model.Name.ToLower() || c.Slug == model.Slug), ct);

        if (duplicate)
        {
            throw new CatalogDomainException(
                "DUPLICATE_CATEGORY_NAME",
                $"Another catalog category named '{model.Name}' already exists.");
        }

        entity.Name = model.Name;
        entity.Slug = model.Slug;
        entity.Description = model.Description;

        await dbContext.SaveChangesAsync(ct);

        var updatedModel = autoMapper.Map<CatalogCategoryEntity, CatalogCategoryDataModel>(entity);
        return autoMapper.Map<CatalogCategoryDataModel, CatalogCategoryDto>(updatedModel);
    }

    public async Task<bool> DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default)
    {
        var entity = await dbContext.Categories
            .Include(c => c.Products)
            .FirstOrDefaultAsync(c => c.Id == categoryId, ct);

        if (entity is null)
        {
            throw new CatalogDomainException("CATEGORY_NOT_FOUND", $"Catalog category {categoryId} was not found.");
        }

        if (entity.Products.Count > 0)
        {
            throw new CatalogDomainException(
                "CATEGORY_IN_USE",
                $"Category '{entity.Name}' cannot be deleted because {entity.Products.Count} product(s) still reference it.");
        }

        dbContext.Categories.Remove(entity);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CatalogCategoryDto>> GetCategoriesAsync(string? search, CancellationToken ct = default)
    {
        IQueryable<CatalogCategoryEntity> query = dbContext.Categories
            .AsNoTracking()
            .Include(c => c.Products);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(c =>
                c.Name.ToLower().Contains(term) ||
                c.Slug.ToLower().Contains(term) ||
                (c.Description != null && c.Description.ToLower().Contains(term)));
        }

        var entities = await query.OrderBy(c => c.Name).ToListAsync(ct);
        return entities
            .Select(e => autoMapper.Map<CatalogCategoryDataModel, CatalogCategoryDto>(
                autoMapper.Map<CatalogCategoryEntity, CatalogCategoryDataModel>(e)))
            .ToList();
    }

    public async Task<CatalogCategoryDto?> GetCategoryByIdAsync(Guid categoryId, CancellationToken ct = default)
    {
        var entity = await dbContext.Categories
            .AsNoTracking()
            .Include(c => c.Products)
            .FirstOrDefaultAsync(c => c.Id == categoryId, ct);

        if (entity is null)
        {
            return null;
        }

        var dataModel = autoMapper.Map<CatalogCategoryEntity, CatalogCategoryDataModel>(entity);
        return autoMapper.Map<CatalogCategoryDataModel, CatalogCategoryDto>(dataModel);
    }

    // =========================================================================
    // 4. INDEPENDENT PRODUCT TAGS & ASSIGNMENTS (3-TABLE PATTERN)
    // =========================================================================

    public async Task<ProductTagDto> CreateProductTagAsync(ProductTagDataModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateAndNormalizeTagModel(model);

        var exists = await dbContext.ProductTags
            .AnyAsync(t => t.Name.ToLower() == model.Name.ToLower(), ct);

        if (exists)
        {
            throw new CatalogDomainException(
                "DUPLICATE_PRODUCT_TAG_NAME",
                $"A product tag named '{model.Name}' already exists.");
        }

        var entity = autoMapper.Map<ProductTagDataModel, ProductTagEntity>(model);
        entity.CreatedAtUtc = DateTime.UtcNow;

        dbContext.ProductTags.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        var savedModel = autoMapper.Map<ProductTagEntity, ProductTagDataModel>(entity);
        return autoMapper.Map<ProductTagDataModel, ProductTagDto>(savedModel);
    }

    public async Task<bool> DeleteProductTagAsync(Guid tagId, CancellationToken ct = default)
    {
        var entity = await dbContext.ProductTags
            .Include(t => t.ProductAssignments)
            .FirstOrDefaultAsync(t => t.Id == tagId, ct);

        if (entity is null)
        {
            throw new CatalogDomainException("PRODUCT_TAG_NOT_FOUND", $"Product tag {tagId} was not found.");
        }

        dbContext.ProductTags.Remove(entity);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ProductTagDto>> GetProductTagsAsync(string? search, CancellationToken ct = default)
    {
        IQueryable<ProductTagEntity> query = dbContext.ProductTags.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(t =>
                t.Name.ToLower().Contains(term) ||
                (t.Value != null && t.Value.ToLower().Contains(term)));
        }

        var entities = await query.OrderBy(t => t.Name).ToListAsync(ct);
        return entities
            .Select(e => autoMapper.Map<ProductTagDataModel, ProductTagDto>(
                autoMapper.Map<ProductTagEntity, ProductTagDataModel>(e)))
            .ToList();
    }

    public async Task<ProductTagDto?> GetProductTagByIdAsync(Guid tagId, CancellationToken ct = default)
    {
        var entity = await dbContext.ProductTags
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tagId, ct);

        if (entity is null)
        {
            return null;
        }

        var dataModel = autoMapper.Map<ProductTagEntity, ProductTagDataModel>(entity);
        return autoMapper.Map<ProductTagDataModel, ProductTagDto>(dataModel);
    }

    public async Task<CatalogProductDto> AssignTagToProductAsync(Guid productId, Guid tagId, CancellationToken ct = default)
    {
        var productEntity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (productEntity is null)
        {
            throw new CatalogDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        var tagEntity = await dbContext.ProductTags.FirstOrDefaultAsync(t => t.Id == tagId, ct);
        if (tagEntity is null)
        {
            throw new CatalogDomainException("PRODUCT_TAG_NOT_FOUND", $"Product tag {tagId} was not found.");
        }

        var alreadyAssigned = productEntity.TagAssignments.Any(a => a.TagId == tagId);
        if (!alreadyAssigned)
        {
            var assignment = new ProductTagAssignmentEntity
            {
                ProductId = productId,
                TagId = tagId,
                Tag = tagEntity,
                AssignedAtUtc = DateTime.UtcNow
            };
            dbContext.ProductTagAssignments.Add(assignment);
            productEntity.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(ct);
        }

        return MapProductEntityToDto(productEntity);
    }

    public async Task<CatalogProductDto> RemoveTagFromProductAsync(Guid productId, Guid tagId, CancellationToken ct = default)
    {
        var productEntity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (productEntity is null)
        {
            throw new CatalogDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        var assignment = productEntity.TagAssignments.FirstOrDefault(a => a.TagId == tagId);
        if (assignment is null)
        {
            throw new CatalogDomainException(
                "PRODUCT_TAG_ASSIGNMENT_NOT_FOUND",
                $"Product tag {tagId} is not assigned to product {productId}.");
        }

        productEntity.TagAssignments.Remove(assignment);
        dbContext.ProductTagAssignments.Remove(assignment);
        productEntity.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        return MapProductEntityToDto(productEntity);
    }

    // =========================================================================
    // 5. MULTI-TIER VOLUME PRICING
    // =========================================================================

    public async Task<CatalogProductDto> SetVolumePriceTiersAsync(
        Guid productId,
        IReadOnlyList<VolumePriceTierDataModel> tiers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        var entity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (entity is null)
        {
            throw new CatalogDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        var validated = ValidateExplicitTiers(tiers, entity.UnitPriceHt);
        ReplaceVolumePriceTiers(entity, validated);
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);

        return MapProductEntityToDto(entity);
    }

    // =========================================================================
    // 6. STOCK OPERATIONS (ENFORCED BACKEND RULES) & REFILL EMAIL ALERTS
    // =========================================================================

    public async Task<CatalogProductDto?> ReserveStockAsync(string sku, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0)
        {
            throw new CatalogDomainException(
                "INVALID_STOCK_OPERATION",
                "Reservation quantity must be strictly positive.");
        }

        var normalizedSku = sku?.Trim().ToUpperInvariant() ?? string.Empty;
        var entity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Sku.ToLower() == normalizedSku.ToLower(), ct);

        if (entity is null)
        {
            return null;
        }

        if (entity.IsArchived)
        {
            throw new CatalogDomainException(
                "PRODUCT_ARCHIVED",
                $"Cannot reserve stock for archived product '{entity.Sku}'.");
        }

        if (entity.AvailableStock <= 0)
        {
            throw new CatalogDomainException(
                "OUT_OF_STOCK",
                $"Cannot reserve '{entity.Sku}': product is currently out of stock.");
        }

        if (quantity > entity.AvailableStock)
        {
            throw new CatalogDomainException(
                "INSUFFICIENT_STOCK",
                $"Cannot reserve {quantity} unit(s) of '{entity.Sku}': only {entity.AvailableStock} available in stock.");
        }

        entity.AvailableStock -= quantity;
        entity.ReservedStock += quantity;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
        return MapProductEntityToDto(entity);
    }

    public async Task<CatalogProductDto?> ReleaseStockAsync(string sku, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0)
        {
            throw new CatalogDomainException(
                "INVALID_STOCK_OPERATION",
                "Release quantity must be strictly positive.");
        }

        var normalizedSku = sku?.Trim().ToUpperInvariant() ?? string.Empty;
        var entity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Sku.ToLower() == normalizedSku.ToLower(), ct);

        if (entity is null)
        {
            return null;
        }

        var previousAvailableStock = entity.AvailableStock;
        var released = Math.Min(entity.ReservedStock, quantity);
        if (released == 0)
        {
            return MapProductEntityToDto(entity);
        }

        entity.ReservedStock -= released;
        entity.AvailableStock += released;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);

        if (previousAvailableStock == 0 && entity.AvailableStock > 0)
        {
            await DispatchPendingRefillAlertsAsync(entity, ct);
        }

        return MapProductEntityToDto(entity);
    }

    public async Task<RestockResultDto> RestockProductAsync(Guid productId, int quantityAdded, CancellationToken ct = default)
    {
        if (quantityAdded <= 0)
        {
            throw new CatalogDomainException(
                "INVALID_STOCK_OPERATION",
                "Restock quantity must be strictly positive.");
        }

        var entity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (entity is null)
        {
            throw new CatalogDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        if (entity.IsArchived)
        {
            throw new CatalogDomainException(
                "PRODUCT_ARCHIVED",
                $"Cannot restock archived product '{entity.Sku}'. Unarchive it first.");
        }

        var previousAvailableStock = entity.AvailableStock;
        entity.AvailableStock += quantityAdded;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);

        var notifiedEmails = previousAvailableStock == 0 && entity.AvailableStock > 0
            ? await DispatchPendingRefillAlertsAsync(entity, ct)
            : [];

        return new RestockResultDto(
            Product: MapProductEntityToDto(entity),
            PreviousAvailableStock: previousAvailableStock,
            DeltaQuantity: quantityAdded,
            RefillAlertsTriggered: notifiedEmails.Count,
            NotifiedEmails: notifiedEmails);
    }

    public async Task<RestockResultDto> AdjustStockAsync(
        Guid productId,
        int newAvailableStock,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (newAvailableStock < 0)
        {
            throw new CatalogDomainException(
                "INVALID_STOCK_OPERATION",
                "Available stock cannot be negative.");
        }

        var entity = await QueryProductsWithIncludes()
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (entity is null)
        {
            throw new CatalogDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        var previousAvailableStock = entity.AvailableStock;
        var delta = newAvailableStock - previousAvailableStock;
        entity.AvailableStock = newAvailableStock;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);

        var notifiedEmails = previousAvailableStock == 0 && entity.AvailableStock > 0 && !entity.IsArchived
            ? await DispatchPendingRefillAlertsAsync(entity, ct)
            : [];

        return new RestockResultDto(
            Product: MapProductEntityToDto(entity),
            PreviousAvailableStock: previousAvailableStock,
            DeltaQuantity: delta,
            RefillAlertsTriggered: notifiedEmails.Count,
            NotifiedEmails: notifiedEmails);
    }

    public async Task<StockRefillAlertDto> SubscribeStockRefillAlertAsync(
        Guid productId,
        string email,
        Guid? customerId = null,
        CancellationToken ct = default)
    {
        var normalizedEmail = ValidateAndNormalizeEmail(email);

        var product = await dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (product is null)
        {
            throw new CatalogDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        if (product.IsArchived)
        {
            throw new CatalogDomainException(
                "PRODUCT_ARCHIVED",
                $"Cannot subscribe to refill alerts for archived product '{product.Sku}'.");
        }

        if (product.AvailableStock > 0)
        {
            throw new CatalogDomainException(
                "PRODUCT_IN_STOCK",
                $"Product '{product.Sku}' is currently in stock ({product.AvailableStock} unit(s) available). Refill alerts are only for out-of-stock products.");
        }

        var duplicatePending = await dbContext.StockRefillAlerts
            .AnyAsync(a => a.ProductId == productId &&
                           !a.IsNotified &&
                           a.Email.ToLower() == normalizedEmail.ToLower(), ct);

        if (duplicatePending)
        {
            throw new CatalogDomainException(
                "DUPLICATE_ALERT_SUBSCRIPTION",
                $"Email '{normalizedEmail}' is already subscribed to a refill alert for '{product.Sku}'.");
        }

        var alertEntity = new StockRefillAlertEntity
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            CustomerId = customerId,
            Email = normalizedEmail,
            IsNotified = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        dbContext.StockRefillAlerts.Add(alertEntity);
        await dbContext.SaveChangesAsync(ct);

        return new StockRefillAlertDto(
            Id: alertEntity.Id,
            ProductId: product.Id,
            Sku: product.Sku,
            ProductName: product.Name,
            CustomerId: alertEntity.CustomerId,
            Email: alertEntity.Email,
            IsNotified: alertEntity.IsNotified,
            CreatedAtUtc: alertEntity.CreatedAtUtc,
            NotifiedAtUtc: alertEntity.NotifiedAtUtc);
    }

    public async Task<bool> UnsubscribeStockRefillAlertAsync(Guid alertId, CancellationToken ct = default)
    {
        var alert = await dbContext.StockRefillAlerts.FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null)
        {
            throw new CatalogDomainException("ALERT_NOT_FOUND", $"Stock refill alert {alertId} was not found.");
        }

        dbContext.StockRefillAlerts.Remove(alert);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<StockRefillAlertDto>> GetProductRefillAlertsAsync(
        Guid productId,
        bool pendingOnly = true,
        CancellationToken ct = default)
    {
        var productExists = await dbContext.Products.AnyAsync(p => p.Id == productId, ct);
        if (!productExists)
        {
            throw new CatalogDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        IQueryable<StockRefillAlertEntity> query = dbContext.StockRefillAlerts
            .AsNoTracking()
            .Include(a => a.Product)
            .Where(a => a.ProductId == productId);

        if (pendingOnly)
        {
            query = query.Where(a => !a.IsNotified);
        }

        var entities = await query.OrderBy(a => a.CreatedAtUtc).ToListAsync(ct);
        return entities
            .Select(e => autoMapper.Map<StockRefillAlertDataModel, StockRefillAlertDto>(
                autoMapper.Map<StockRefillAlertEntity, StockRefillAlertDataModel>(e)))
            .ToList();
    }

    // =========================================================================
    // 7. STATIC VALIDATION & SEEDING HELPERS
    // =========================================================================

    public static void ValidateAndNormalizeProductModel(CatalogProductDataModel model, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (isCreate)
        {
            model.Sku = model.Sku?.Trim().ToUpperInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model.Sku))
            {
                throw new CatalogDomainException("INVALID_PRODUCT_DATA", "Product SKU is required.");
            }
        }

        model.Name = model.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            throw new CatalogDomainException("INVALID_PRODUCT_DATA", "Product name is required.");
        }

        model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        model.CategoryName = model.CategoryName?.Trim() ?? string.Empty;

        if (model.CategoryId == Guid.Empty && string.IsNullOrWhiteSpace(model.CategoryName))
        {
            throw new CatalogDomainException("INVALID_PRODUCT_DATA", "Product category (CategoryId or Category name) is required.");
        }

        if (model.UnitPriceHt <= 0m)
        {
            throw new CatalogDomainException("INVALID_PRODUCT_DATA", "UnitPriceHt must be strictly positive.");
        }

        if (model.BulkUnitPriceHt < 0m)
        {
            throw new CatalogDomainException("INVALID_PRODUCT_DATA", "BulkUnitPriceHt cannot be negative.");
        }

        if (model.BulkUnitPriceHt > model.UnitPriceHt)
        {
            throw new CatalogDomainException("INVALID_PRODUCT_DATA", "BulkUnitPriceHt cannot exceed standard UnitPriceHt.");
        }

        if (model.AvailableStock < 0)
        {
            throw new CatalogDomainException("INVALID_PRODUCT_DATA", "Initial/Available stock cannot be negative.");
        }

        if (model.LowStockThreshold < 0)
        {
            throw new CatalogDomainException("INVALID_PRODUCT_DATA", "LowStockThreshold cannot be negative.");
        }

        if (model.UnitWeightKg <= 0d)
        {
            throw new CatalogDomainException("INVALID_PRODUCT_DATA", "UnitWeightKg must be strictly positive.");
        }
    }

    public static void ValidateAndNormalizeCategoryModel(CatalogCategoryDataModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Name = model.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            throw new CatalogDomainException("INVALID_CATEGORY_DATA", "Category name is required.");
        }

        model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        model.Slug = GenerateSlug(model.Name);
    }

    public static void ValidateAndNormalizeTagModel(ProductTagDataModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Name = model.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            throw new CatalogDomainException("INVALID_TAG_DATA", "Product tag name is required.");
        }

        model.Value = string.IsNullOrWhiteSpace(model.Value) ? null : model.Value.Trim();
        model.Color = string.IsNullOrWhiteSpace(model.Color) ? "#0EA5E9" : model.Color.Trim();
    }

    public static string GenerateSlug(string name)
    {
        var normalized = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var lastWasDash = false;

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        return sb.ToString().TrimEnd('-');
    }

    public static void SeedDefaultCatalog(CatalogDbContext db)
    {
        if (db.Products.Any())
        {
            return;
        }

        var now = DateTime.UtcNow;

        var datacenterCat = new CatalogCategoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "Datacenter Infrastructure",
            Slug = "datacenter-infrastructure",
            Description = "High-density server racks, cabinets, and liquid cooling hardware.",
            CreatedAtUtc = now
        };

        var networkingCat = new CatalogCategoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "Networking",
            Slug = "networking",
            Description = "Enterprise fiber switches, optical transceivers, and core routers.",
            CreatedAtUtc = now
        };

        var powerCat = new CatalogCategoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "Power & Energy",
            Slug = "power-energy",
            Description = "Industrial double-conversion UPS units and redundant PDUs.",
            CreatedAtUtc = now
        };

        db.Categories.AddRange(datacenterCat, networkingCat, powerCat);

        var rack = new CatalogProductEntity
        {
            Id = Guid.NewGuid(),
            Sku = "SRV-RACK-42U",
            Name = "Baie Serveur Haute Densité 42U refroidissement liquide",
            Description = "Baie 42U refroidissement liquide directe pour clusters IA et datacenters Tier-IV.",
            CategoryId = datacenterCat.Id,
            Category = datacenterCat,
            UnitPriceHt = 2400m,
            BulkTierMinQuantity = 5,
            BulkUnitPriceHt = 2040m,
            AvailableStock = 40,
            ReservedStock = 2,
            LowStockThreshold = 15,
            UnitWeightKg = 85.0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        rack.VolumePriceTiers.Add(new ProductVolumePriceTierEntity { ProductId = rack.Id, MinQuantity = 5, UnitPriceHt = 2040m });
        rack.VolumePriceTiers.Add(new ProductVolumePriceTierEntity { ProductId = rack.Id, MinQuantity = 15, UnitPriceHt = 1890m });

        var fiberSwitch = new CatalogProductEntity
        {
            Id = Guid.NewGuid(),
            Sku = "SW-FIBER-100G",
            Name = "Switch Fibre Optique 100GbE 32 Ports L3",
            Description = "Commutateur cœur de réseau 100GbE faible latence avec double alimentation redondante.",
            CategoryId = networkingCat.Id,
            Category = networkingCat,
            UnitPriceHt = 4800m,
            BulkTierMinQuantity = 4,
            BulkUnitPriceHt = 4200m,
            AvailableStock = 28,
            ReservedStock = 0,
            LowStockThreshold = 15,
            UnitWeightKg = 9.5,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        fiberSwitch.VolumePriceTiers.Add(new ProductVolumePriceTierEntity { ProductId = fiberSwitch.Id, MinQuantity = 4, UnitPriceHt = 4200m });
        fiberSwitch.VolumePriceTiers.Add(new ProductVolumePriceTierEntity { ProductId = fiberSwitch.Id, MinQuantity = 10, UnitPriceHt = 3850m });

        var ups = new CatalogProductEntity
        {
            Id = Guid.NewGuid(),
            Sku = "UPS-LFP-10KVA",
            Name = "Onduleur Industriel LiFePO4 10kVA Double Conversion",
            Description = "Onduleur on-line double conversion à batteries LiFePO4 longue durée.",
            CategoryId = powerCat.Id,
            Category = powerCat,
            UnitPriceHt = 1950m,
            BulkTierMinQuantity = 6,
            BulkUnitPriceHt = 1650m,
            AvailableStock = 12,
            ReservedStock = 1,
            LowStockThreshold = 15,
            UnitWeightKg = 42.0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        ups.VolumePriceTiers.Add(new ProductVolumePriceTierEntity { ProductId = ups.Id, MinQuantity = 6, UnitPriceHt = 1650m });
        ups.VolumePriceTiers.Add(new ProductVolumePriceTierEntity { ProductId = ups.Id, MinQuantity = 12, UnitPriceHt = 1500m });

        db.Products.AddRange(rack, fiberSwitch, ups);
        db.SaveChanges();
    }

    // =========================================================================
    // 8. PRIVATE HELPERS
    // =========================================================================

    private IQueryable<CatalogProductEntity> QueryProductsWithIncludes() =>
        dbContext.Products
            .Include(p => p.Category)
            .Include(p => p.VolumePriceTiers)
            .Include(p => p.TagAssignments)
                .ThenInclude(a => a.Tag);

    private CatalogProductDto MapProductEntityToDto(CatalogProductEntity entity)
    {
        var dataModel = autoMapper.Map<CatalogProductEntity, CatalogProductDataModel>(entity);
        return autoMapper.Map<CatalogProductDataModel, CatalogProductDto>(dataModel);
    }

    private async Task<CatalogCategoryEntity> ResolveOrCreateCategoryAsync(
        Guid categoryId,
        string categoryName,
        CancellationToken ct)
    {
        if (categoryId != Guid.Empty)
        {
            var byId = await dbContext.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
            if (byId is null)
            {
                throw new CatalogDomainException("CATEGORY_NOT_FOUND", $"Catalog category {categoryId} was not found.");
            }

            return byId;
        }

        var trimmedName = categoryName.Trim();
        var slug = GenerateSlug(trimmedName);
        var existing = await dbContext.Categories
            .FirstOrDefaultAsync(c => c.Name.ToLower() == trimmedName.ToLower() || c.Slug == slug, ct);

        if (existing is not null)
        {
            return existing;
        }

        var created = new CatalogCategoryEntity
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            Slug = slug,
            CreatedAtUtc = DateTime.UtcNow
        };
        dbContext.Categories.Add(created);
        return created;
    }

    private static List<VolumePriceTierDataModel> NormalizeAndValidateTiers(CatalogProductDataModel model)
    {
        if (model.VolumePriceTiers.Count > 0)
        {
            return ValidateExplicitTiers(model.VolumePriceTiers, model.UnitPriceHt);
        }

        if (model.BulkTierMinQuantity >= 2 && model.BulkUnitPriceHt > 0m)
        {
            return
            [
                new VolumePriceTierDataModel
                {
                    Id = Guid.NewGuid(),
                    MinQuantity = model.BulkTierMinQuantity,
                    UnitPriceHt = model.BulkUnitPriceHt
                }
            ];
        }

        return [];
    }

    private static List<VolumePriceTierDataModel> ValidateExplicitTiers(
        IReadOnlyList<VolumePriceTierDataModel> tiers,
        decimal baseUnitPriceHt)
    {
        var result = new List<VolumePriceTierDataModel>(tiers.Count);
        var seenQuantities = new HashSet<int>();

        foreach (var tier in tiers.OrderBy(t => t.MinQuantity))
        {
            if (tier.MinQuantity < 2)
            {
                throw new CatalogDomainException(
                    "INVALID_VOLUME_TIER",
                    $"Volume pricing tier MinQuantity ({tier.MinQuantity}) must be at least 2.");
            }

            if (tier.UnitPriceHt <= 0m || tier.UnitPriceHt > baseUnitPriceHt)
            {
                throw new CatalogDomainException(
                    "INVALID_VOLUME_TIER",
                    $"Volume pricing tier UnitPriceHt ({tier.UnitPriceHt}) must be > 0 and <= base UnitPriceHt ({baseUnitPriceHt}).");
            }

            if (!seenQuantities.Add(tier.MinQuantity))
            {
                throw new CatalogDomainException(
                    "DUPLICATE_VOLUME_TIER",
                    $"Duplicate volume pricing tier for MinQuantity {tier.MinQuantity}.");
            }

            result.Add(new VolumePriceTierDataModel
            {
                Id = tier.Id == Guid.Empty ? Guid.NewGuid() : tier.Id,
                MinQuantity = tier.MinQuantity,
                UnitPriceHt = Math.Round(tier.UnitPriceHt, 2)
            });
        }

        return result;
    }

    private static void SyncPrimaryBulkFieldsFromTiers(
        CatalogProductEntity entity,
        List<VolumePriceTierDataModel> tiers)
    {
        if (tiers.Count > 0)
        {
            var first = tiers[0];
            entity.BulkTierMinQuantity = first.MinQuantity;
            entity.BulkUnitPriceHt = first.UnitPriceHt;
        }
    }

    private void ReplaceVolumePriceTiers(
        CatalogProductEntity entity,
        List<VolumePriceTierDataModel> newTiers)
    {
        if (entity.VolumePriceTiers.Count > 0)
        {
            dbContext.VolumePriceTiers.RemoveRange(entity.VolumePriceTiers);
            entity.VolumePriceTiers.Clear();
        }

        foreach (var tier in newTiers)
        {
            var tierEntity = new ProductVolumePriceTierEntity
            {
                Id = Guid.NewGuid(),
                ProductId = entity.Id,
                MinQuantity = tier.MinQuantity,
                UnitPriceHt = tier.UnitPriceHt
            };
            dbContext.VolumePriceTiers.Add(tierEntity);
        }

        SyncPrimaryBulkFieldsFromTiers(entity, newTiers);
    }

    private async Task<List<ProductTagEntity>> ResolveIndependentTagsForProductAsync(
        CatalogProductDataModel productModel,
        CancellationToken ct)
    {
        var resolved = new List<ProductTagEntity>();
        var seenTagIds = new HashSet<Guid>();

        if (productModel.TagIds.Count > 0)
        {
            var distinctIds = productModel.TagIds.Where(id => id != Guid.Empty).Distinct().ToList();
            var existingById = await dbContext.ProductTags
                .Where(t => distinctIds.Contains(t.Id))
                .ToListAsync(ct);

            if (existingById.Count != distinctIds.Count)
            {
                var missingId = distinctIds.First(id => existingById.All(t => t.Id != id));
                throw new CatalogDomainException("PRODUCT_TAG_NOT_FOUND", $"Product tag {missingId} was not found.");
            }

            resolved.AddRange(existingById.Where(t => seenTagIds.Add(t.Id)));
        }

        var seenNames = new HashSet<string>(
            resolved.Select(r => r.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var rawTag in productModel.Tags)
        {
            var trimmedName = rawTag.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName) || !seenNames.Add(trimmedName))
            {
                continue;
            }

            var existingByName = await dbContext.ProductTags
                .FirstOrDefaultAsync(t => t.Name.ToLower() == trimmedName.ToLower(), ct);

            if (existingByName is not null)
            {
                if (seenTagIds.Add(existingByName.Id))
                {
                    resolved.Add(existingByName);
                }
            }
            else
            {
                ValidateAndNormalizeTagModel(rawTag);
                var newTagEntity = autoMapper.Map<ProductTagDataModel, ProductTagEntity>(rawTag);
                dbContext.ProductTags.Add(newTagEntity);
                seenTagIds.Add(newTagEntity.Id);
                resolved.Add(newTagEntity);
            }
        }

        return resolved;
    }

    private void ReplaceTagAssignments(
        CatalogProductEntity entity,
        List<ProductTagEntity> newTags)
    {
        if (entity.TagAssignments.Count > 0)
        {
            dbContext.ProductTagAssignments.RemoveRange(entity.TagAssignments);
            entity.TagAssignments.Clear();
        }

        var now = DateTime.UtcNow;
        foreach (var tag in newTags)
        {
            var assignment = new ProductTagAssignmentEntity
            {
                ProductId = entity.Id,
                TagId = tag.Id,
                Tag = tag,
                AssignedAtUtc = now
            };
            dbContext.ProductTagAssignments.Add(assignment);
        }
    }

    private async Task<IReadOnlyList<string>> DispatchPendingRefillAlertsAsync(
        CatalogProductEntity product,
        CancellationToken ct)
    {
        var pendingAlerts = await dbContext.StockRefillAlerts
            .Where(a => a.ProductId == product.Id && !a.IsNotified)
            .OrderBy(a => a.CreatedAtUtc)
            .ToListAsync(ct);

        if (pendingAlerts.Count == 0)
        {
            return [];
        }

        var notifiedEmails = new List<string>(pendingAlerts.Count);
        var now = DateTime.UtcNow;

        foreach (var alert in pendingAlerts)
        {
            var subject = $"[B-Lightful Hub] Back in stock: {product.Name} ({product.Sku})";
            var body = $"Good news! Product '{product.Name}' (SKU: {product.Sku}) has been restocked and now has {product.AvailableStock} unit(s) available at {product.UnitPriceHt:F2} EUR HT.";
            var metadata = new Dictionary<string, string>
            {
                ["ProductId"] = product.Id.ToString(),
                ["Sku"] = product.Sku,
                ["AvailableStock"] = product.AvailableStock.ToString(CultureInfo.InvariantCulture)
            };

            await emailNotificationService.SendEmailAsync(
                toEmail: alert.Email,
                subject: subject,
                body: body,
                category: "CatalogStockRefillAlert",
                metadata: metadata,
                ct: ct);

            alert.IsNotified = true;
            alert.NotifiedAtUtc = now;
            notifiedEmails.Add(alert.Email);
        }

        await dbContext.SaveChangesAsync(ct);
        return notifiedEmails;
    }

    private static string ValidateAndNormalizeEmail(string? rawEmail)
    {
        var trimmed = rawEmail?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) || !BasicEmailRegex.IsMatch(trimmed))
        {
            throw new CatalogDomainException("INVALID_EMAIL", "A valid email address is required for refill alerts.");
        }

        return trimmed.ToLowerInvariant();
    }
}
