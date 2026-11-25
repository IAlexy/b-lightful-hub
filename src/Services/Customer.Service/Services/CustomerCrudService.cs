namespace Customer.Service.Services;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Catalog.Service;
using Catalog.Service.Services;
using Customer.Service.DataModels;
using Customer.Service.Entities;
using Customer.Service.Persistence;
using Delivery.Service;
using Invoicing.Service;
using Microsoft.EntityFrameworkCore;
using Ordering.Service;

public interface ICustomerService
{
    // Customer CRUD
    Task<B2BCustomerDto> CreateCustomerAsync(CustomerAccountDataModel customerModel, CancellationToken ct = default);
    Task<IReadOnlyList<B2BCustomerDto>> GetCustomersAsync(string? search = null, string? tag = null, CancellationToken ct = default);
    Task<B2BCustomerDto?> GetCustomerByIdAsync(Guid customerId, CancellationToken ct = default);
    Task<B2BCustomerDto> UpdateCustomerAsync(CustomerAccountDataModel customerModel, CancellationToken ct = default);
    Task<bool> DeleteCustomerAsync(Guid customerId, CancellationToken ct = default);

    // Independent Custom Tags CRUD & Customer <-> Tag Associations (3rd Table)
    Task<CustomTagDto> CreateCustomTagAsync(CustomTagDataModel tagModel, CancellationToken ct = default);
    Task<IReadOnlyList<CustomTagDto>> GetCustomTagsAsync(string? search = null, CancellationToken ct = default);
    Task<CustomTagDto?> GetCustomTagByIdAsync(Guid tagId, CancellationToken ct = default);
    Task<bool> DeleteCustomTagAsync(Guid tagId, CancellationToken ct = default);
    Task<B2BCustomerDto> AssignTagToCustomerAsync(CustomerTagAssignmentDataModel assignmentModel, CancellationToken ct = default);
    Task<B2BCustomerDto> RemoveTagFromCustomerAsync(CustomerTagAssignmentDataModel assignmentModel, CancellationToken ct = default);

    // Customer 360° Cross-Service Queries
    Task<IReadOnlyList<PurchaseOrderDto>> GetCustomerOrdersAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<B2BInvoiceDto>> GetCustomerInvoicesAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<B2BShipmentDto>> GetCustomerDeliveriesAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerFavoriteProductDto>> GetCustomerFavoriteProductsAsync(Guid customerId, CancellationToken ct = default);

    // Manual Favorite Products & Personal Wishlist
    Task<CustomerFavoriteProductDto> AddManualFavoriteProductAsync(Guid customerId, Guid productId, CancellationToken ct = default);
    Task<bool> RemoveManualFavoriteProductAsync(Guid customerId, Guid productId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerWishlistItemDto>> GetCustomerWishlistAsync(Guid customerId, CancellationToken ct = default);
    Task<CustomerWishlistItemDto> AddProductToWishlistAsync(Guid customerId, Guid productId, string? note = null, int priority = 3, CancellationToken ct = default);
    Task<CustomerWishlistItemDto> UpdateWishlistItemAsync(Guid customerId, Guid productId, string? note, int priority, CancellationToken ct = default);
    Task<bool> RemoveProductFromWishlistAsync(Guid customerId, Guid productId, CancellationToken ct = default);
}

public sealed class CustomerCrudService(
    CustomerDbContext dbContext,
    IMapper autoMapper,
    ISender sender,
    ICatalogService catalogService) : ICustomerService
{
    // =========================================================================
    // 1. CUSTOMER CRUD & BUSINESS RULES
    // =========================================================================

    public async Task<B2BCustomerDto> CreateCustomerAsync(
        CustomerAccountDataModel customerModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(customerModel);
        ValidateAndApplyCustomerRules(customerModel);

        var duplicateExists = await dbContext.Customers
            .AnyAsync(c => c.SirenSiret == customerModel.SirenSiret, ct);

        if (duplicateExists)
        {
            throw new CustomerDomainException(
                "DUPLICATE_SIREN_SIRET",
                $"A customer with SIREN/SIRET '{customerModel.SirenSiret}' already exists.");
        }

        var entity = autoMapper.Map<CustomerAccountDataModel, CustomerProfileEntity>(customerModel);
        dbContext.Customers.Add(entity);

        // Resolve independent CustomTags (by TagIds or inline Tags) and create rows in the 3rd table ("CustomerTagAssignments")
        var resolvedTagEntities = await ResolveIndependentTagsForCustomerAsync(customerModel, ct);
        foreach (var tagEntity in resolvedTagEntities)
        {
            var assignment = new CustomerTagAssignmentEntity
            {
                CustomerId = entity.Id,
                Customer = entity,
                TagId = tagEntity.Id,
                Tag = tagEntity,
                AssignedAtUtc = DateTime.UtcNow
            };
            entity.TagAssignments.Add(assignment);
            dbContext.CustomerTagAssignments.Add(assignment);
        }

        await dbContext.SaveChangesAsync(ct);

        return MapCustomerEntityToDto(entity);
    }

    public async Task<IReadOnlyList<B2BCustomerDto>> GetCustomersAsync(
        string? search = null,
        string? tag = null,
        CancellationToken ct = default)
    {
        var entities = await dbContext.Customers
            .AsNoTracking()
            .Include(c => c.TagAssignments)
            .ThenInclude(a => a.Tag)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        IEnumerable<CustomerProfileEntity> filtered = entities;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(c =>
                c.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.SirenSiret.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.InvoicingAddress.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.DeliveryAddress.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (c.ContactEmail is not null && c.ContactEmail.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (c.PhoneNumber is not null && c.PhoneNumber.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var tagTerm = tag.Trim();
            filtered = filtered.Where(c =>
                c.TagAssignments.Any(a =>
                    a.Tag is not null &&
                    (a.Tag.Name.Equals(tagTerm, StringComparison.OrdinalIgnoreCase) ||
                     (a.Tag.Value is not null && a.Tag.Value.Contains(tagTerm, StringComparison.OrdinalIgnoreCase)))));
        }

        return filtered.Select(MapCustomerEntityToDto).ToList();
    }

    public async Task<B2BCustomerDto?> GetCustomerByIdAsync(Guid customerId, CancellationToken ct = default)
    {
        var entity = await dbContext.Customers
            .AsNoTracking()
            .Include(c => c.TagAssignments)
            .ThenInclude(a => a.Tag)
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);

        return entity is null ? null : MapCustomerEntityToDto(entity);
    }

    public async Task<B2BCustomerDto> UpdateCustomerAsync(
        CustomerAccountDataModel customerModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(customerModel);
        ValidateAndApplyCustomerRules(customerModel);

        var existing = await dbContext.Customers
            .Include(c => c.TagAssignments)
            .ThenInclude(a => a.Tag)
            .FirstOrDefaultAsync(c => c.Id == customerModel.Id, ct);

        if (existing is null)
        {
            throw new CustomerDomainException(
                "CUSTOMER_NOT_FOUND",
                $"Customer {customerModel.Id} was not found.");
        }

        var duplicateSiren = await dbContext.Customers
            .AnyAsync(c => c.Id != customerModel.Id && c.SirenSiret == customerModel.SirenSiret, ct);

        if (duplicateSiren)
        {
            throw new CustomerDomainException(
                "DUPLICATE_SIREN_SIRET",
                $"Another customer with SIREN/SIRET '{customerModel.SirenSiret}' already exists.");
        }

        existing.Name = customerModel.Name;
        existing.SirenSiret = customerModel.SirenSiret;
        existing.InvoicingAddress = customerModel.InvoicingAddress;
        existing.DeliveryAddress = customerModel.DeliveryAddress;
        existing.UseInvoicingAddressForDelivery = customerModel.UseInvoicingAddressForDelivery;
        existing.ContactEmail = customerModel.ContactEmail;
        existing.PhoneNumber = customerModel.PhoneNumber;
        existing.UpdatedAtUtc = DateTime.UtcNow;

        // If TagIds or inline Tags were supplied on update, synchronize the 3rd table ("CustomerTagAssignments")
        // without deleting the independent tags from "CustomTags"
        if (customerModel.TagIds.Count > 0 || customerModel.Tags.Count > 0)
        {
            dbContext.CustomerTagAssignments.RemoveRange(existing.TagAssignments);
            existing.TagAssignments.Clear();

            var resolvedTags = await ResolveIndependentTagsForCustomerAsync(customerModel, ct);
            foreach (var tagEntity in resolvedTags)
            {
                var assignment = new CustomerTagAssignmentEntity
                {
                    CustomerId = existing.Id,
                    Customer = existing,
                    TagId = tagEntity.Id,
                    Tag = tagEntity,
                    AssignedAtUtc = DateTime.UtcNow
                };
                existing.TagAssignments.Add(assignment);
                dbContext.CustomerTagAssignments.Add(assignment);
            }
        }

        await dbContext.SaveChangesAsync(ct);

        return MapCustomerEntityToDto(existing);
    }

    public async Task<bool> DeleteCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        var existing = await dbContext.Customers
            .Include(c => c.TagAssignments)
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);

        if (existing is null)
        {
            throw new CustomerDomainException(
                "CUSTOMER_NOT_FOUND",
                $"Customer {customerId} was not found.");
        }

        dbContext.Customers.Remove(existing);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    // =========================================================================
    // 2. INDEPENDENT CUSTOM TAGS CRUD & 3RD TABLE ASSOCIATIONS
    // =========================================================================

    public async Task<CustomTagDto> CreateCustomTagAsync(CustomTagDataModel tagModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagModel);
        ValidateAndNormalizeTagModel(tagModel);

        var duplicateTag = await dbContext.CustomTags
            .AnyAsync(t => t.Name.ToLower() == tagModel.Name.ToLower(), ct);

        if (duplicateTag)
        {
            throw new CustomerDomainException(
                "DUPLICATE_TAG_NAME",
                $"A custom tag named '{tagModel.Name}' already exists.");
        }

        var entity = autoMapper.Map<CustomTagDataModel, CustomTagEntity>(tagModel);
        dbContext.CustomTags.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        var savedModel = autoMapper.Map<CustomTagEntity, CustomTagDataModel>(entity);
        return autoMapper.Map<CustomTagDataModel, CustomTagDto>(savedModel);
    }

    public async Task<IReadOnlyList<CustomTagDto>> GetCustomTagsAsync(string? search = null, CancellationToken ct = default)
    {
        var tags = await dbContext.CustomTags
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        IEnumerable<CustomTagEntity> filtered = tags;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(t =>
                t.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (t.Value is not null && t.Value.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var models = autoMapper.Map<IEnumerable<CustomTagEntity>, List<CustomTagDataModel>>(filtered);
        return autoMapper.Map<List<CustomTagDataModel>, List<CustomTagDto>>(models);
    }

    public async Task<CustomTagDto?> GetCustomTagByIdAsync(Guid tagId, CancellationToken ct = default)
    {
        var entity = await dbContext.CustomTags.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tagId, ct);
        if (entity is null)
        {
            return null;
        }

        var model = autoMapper.Map<CustomTagEntity, CustomTagDataModel>(entity);
        return autoMapper.Map<CustomTagDataModel, CustomTagDto>(model);
    }

    public async Task<bool> DeleteCustomTagAsync(Guid tagId, CancellationToken ct = default)
    {
        var entity = await dbContext.CustomTags.FirstOrDefaultAsync(t => t.Id == tagId, ct);
        if (entity is null)
        {
            throw new CustomerDomainException("TAG_NOT_FOUND", $"Custom tag {tagId} was not found.");
        }

        dbContext.CustomTags.Remove(entity);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<B2BCustomerDto> AssignTagToCustomerAsync(
        CustomerTagAssignmentDataModel assignmentModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignmentModel);

        var customer = await dbContext.Customers
            .Include(c => c.TagAssignments)
            .ThenInclude(a => a.Tag)
            .FirstOrDefaultAsync(c => c.Id == assignmentModel.CustomerId, ct);

        if (customer is null)
        {
            throw new CustomerDomainException(
                "CUSTOMER_NOT_FOUND",
                $"Customer {assignmentModel.CustomerId} was not found.");
        }

        var tag = await dbContext.CustomTags.FirstOrDefaultAsync(t => t.Id == assignmentModel.TagId, ct);
        if (tag is null)
        {
            throw new CustomerDomainException(
                "TAG_NOT_FOUND",
                $"Custom tag {assignmentModel.TagId} was not found.");
        }

        var alreadyAssigned = customer.TagAssignments.Any(a => a.TagId == tag.Id);
        if (!alreadyAssigned)
        {
            var assignmentEntity = autoMapper.Map<CustomerTagAssignmentDataModel, CustomerTagAssignmentEntity>(assignmentModel);
            assignmentEntity.Customer = customer;
            assignmentEntity.Tag = tag;
            assignmentEntity.AssignedAtUtc = DateTime.UtcNow;

            customer.TagAssignments.Add(assignmentEntity);
            dbContext.CustomerTagAssignments.Add(assignmentEntity);
            customer.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(ct);
        }

        return MapCustomerEntityToDto(customer);
    }

    public async Task<B2BCustomerDto> RemoveTagFromCustomerAsync(
        CustomerTagAssignmentDataModel assignmentModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignmentModel);

        var customer = await dbContext.Customers
            .Include(c => c.TagAssignments)
            .ThenInclude(a => a.Tag)
            .FirstOrDefaultAsync(c => c.Id == assignmentModel.CustomerId, ct);

        if (customer is null)
        {
            throw new CustomerDomainException(
                "CUSTOMER_NOT_FOUND",
                $"Customer {assignmentModel.CustomerId} was not found.");
        }

        var assignment = customer.TagAssignments.FirstOrDefault(a => a.TagId == assignmentModel.TagId);
        if (assignment is null)
        {
            throw new CustomerDomainException(
                "TAG_ASSIGNMENT_NOT_FOUND",
                $"Tag {assignmentModel.TagId} is not assigned to customer {assignmentModel.CustomerId}.");
        }

        customer.TagAssignments.Remove(assignment);
        dbContext.CustomerTagAssignments.Remove(assignment);
        customer.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        return MapCustomerEntityToDto(customer);
    }

    // =========================================================================
    // 3. CUSTOMER 360° CROSS-SERVICE QUERIES
    // =========================================================================

    public async Task<IReadOnlyList<PurchaseOrderDto>> GetCustomerOrdersAsync(Guid customerId, CancellationToken ct = default)
    {
        var customer = await RequireCustomerEntityAsync(customerId, ct);
        var allOrders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(
            new GetPurchaseOrdersQuery(), ct);

        return allOrders
            .Where(o => MatchesCustomer(o.CustomerId, o.BuyerVatNumber, o.BuyerCompanyName, customer))
            .ToList();
    }

    public async Task<IReadOnlyList<B2BInvoiceDto>> GetCustomerInvoicesAsync(Guid customerId, CancellationToken ct = default)
    {
        var customer = await RequireCustomerEntityAsync(customerId, ct);
        var customerOrders = await GetCustomerOrdersAsync(customerId, ct);
        var customerOrderIds = customerOrders.Select(o => o.Id).ToHashSet();

        var allInvoices = await sender.SendQueryAsync<GetInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>(
            new GetInvoicesQuery(), ct);

        return allInvoices
            .Where(inv =>
                customerOrderIds.Contains(inv.OrderId) ||
                MatchesCustomer(null, inv.BuyerVatNumber, inv.BuyerCompanyName, customer))
            .ToList();
    }

    public async Task<IReadOnlyList<B2BShipmentDto>> GetCustomerDeliveriesAsync(Guid customerId, CancellationToken ct = default)
    {
        var customer = await RequireCustomerEntityAsync(customerId, ct);
        var customerOrders = await GetCustomerOrdersAsync(customerId, ct);
        var customerOrderIds = customerOrders.Select(o => o.Id).ToHashSet();

        var allShipments = await sender.SendQueryAsync<GetShipmentsQuery, IReadOnlyList<B2BShipmentDto>>(
            new GetShipmentsQuery(), ct);

        return allShipments
            .Where(s =>
                customerOrderIds.Contains(s.OrderId) ||
                string.Equals(s.DestinationAddress, customer.DeliveryAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<IReadOnlyList<CustomerFavoriteProductDto>> GetCustomerFavoriteProductsAsync(
        Guid customerId,
        CancellationToken ct = default)
    {
        await RequireCustomerEntityAsync(customerId, ct);

        // 1. Aggregate frequently ordered SKUs from customer's non-cancelled Purchase Orders
        var orders = await GetCustomerOrdersAsync(customerId, ct);
        var orderStatsBySku = orders
            .Where(o => !string.Equals(o.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            .SelectMany(o => o.Lines.Select(l => new { o.Id, o.CreatedAtUtc, Line = l }))
            .GroupBy(x => x.Line.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key.ToUpperInvariant(),
                g => (
                    OrderFrequency: g.Select(x => x.Id).Distinct().Count(),
                    TotalUnitsOrdered: g.Sum(x => x.Line.Quantity),
                    LastOrderedAtUtc: (DateTime?)g.Max(x => x.CreatedAtUtc),
                    FallbackName: g.First().Line.ProductName,
                    FallbackUnitPriceHt: g.First().Line.UnitPriceHt
                ),
                StringComparer.OrdinalIgnoreCase);

        // 2. Load customer's manually pinned favorite products
        var manualFavorites = await dbContext.ManualFavorites
            .AsNoTracking()
            .Where(f => f.CustomerId == customerId)
            .ToListAsync(ct);

        var manualFavBySku = manualFavorites
            .ToDictionary(f => f.Sku.ToUpperInvariant(), f => f, StringComparer.OrdinalIgnoreCase);

        // 3. Merge union of ordered SKUs & manual favorites and enrich with live Catalog data
        var allSkus = orderStatsBySku.Keys
            .Union(manualFavBySku.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<CustomerFavoriteProductDto>(allSkus.Count);
        foreach (var sku in allSkus)
        {
            var hasOrderStats = orderStatsBySku.TryGetValue(sku, out var stats);
            var isManual = manualFavBySku.TryGetValue(sku, out var manualEntity);
            var catalogProduct = await catalogService.GetProductBySkuAsync(sku, includeArchived: true, ct);

            results.Add(new CustomerFavoriteProductDto(
                CustomerId: customerId,
                ProductId: catalogProduct?.Id ?? manualEntity?.ProductId,
                Sku: catalogProduct?.Sku ?? sku,
                ProductName: catalogProduct?.Name ?? (hasOrderStats ? stats.FallbackName : sku),
                Category: catalogProduct?.Category,
                UnitPriceHt: catalogProduct?.UnitPriceHt ?? (hasOrderStats ? stats.FallbackUnitPriceHt : 0m),
                AvailableStock: catalogProduct?.AvailableStock ?? 0,
                IsOutOfStock: catalogProduct?.IsOutOfStock ?? false,
                IsArchived: catalogProduct?.IsArchived ?? false,
                OrderFrequency: hasOrderStats ? stats.OrderFrequency : 0,
                TotalUnitsOrdered: hasOrderStats ? stats.TotalUnitsOrdered : 0,
                IsManualFavorite: isManual,
                LastOrderedAtUtc: hasOrderStats ? stats.LastOrderedAtUtc : null));
        }

        return results
            .OrderByDescending(f => f.OrderFrequency)
            .ThenByDescending(f => f.IsManualFavorite)
            .ThenByDescending(f => f.TotalUnitsOrdered)
            .ThenBy(f => f.Sku, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // =========================================================================
    // 3.5 MANUAL FAVORITE PRODUCTS & PERSONAL WISHLIST
    // =========================================================================

    public async Task<CustomerFavoriteProductDto> AddManualFavoriteProductAsync(
        Guid customerId,
        Guid productId,
        CancellationToken ct = default)
    {
        await RequireCustomerEntityAsync(customerId, ct);

        var product = await RequireActiveCatalogProductAsync(productId, ct);

        var exists = await dbContext.ManualFavorites
            .AnyAsync(f => f.CustomerId == customerId && f.ProductId == productId, ct);

        if (exists)
        {
            throw new CustomerDomainException(
                "DUPLICATE_FAVORITE",
                $"Product '{product.Sku}' is already in customer {customerId}'s manual favorites.");
        }

        dbContext.ManualFavorites.Add(new CustomerManualFavoriteEntity
        {
            CustomerId = customerId,
            ProductId = product.Id,
            Sku = product.Sku,
            AddedAtUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync(ct);

        var allFavorites = await GetCustomerFavoriteProductsAsync(customerId, ct);
        return allFavorites.First(f => f.ProductId == product.Id);
    }

    public async Task<bool> RemoveManualFavoriteProductAsync(
        Guid customerId,
        Guid productId,
        CancellationToken ct = default)
    {
        await RequireCustomerEntityAsync(customerId, ct);

        var entity = await dbContext.ManualFavorites
            .FirstOrDefaultAsync(f => f.CustomerId == customerId && f.ProductId == productId, ct);

        if (entity is null)
        {
            throw new CustomerDomainException(
                "FAVORITE_NOT_FOUND",
                $"Product {productId} is not in customer {customerId}'s manual favorites.");
        }

        dbContext.ManualFavorites.Remove(entity);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CustomerWishlistItemDto>> GetCustomerWishlistAsync(
        Guid customerId,
        CancellationToken ct = default)
    {
        await RequireCustomerEntityAsync(customerId, ct);

        var entities = await dbContext.WishlistItems
            .AsNoTracking()
            .Where(w => w.CustomerId == customerId)
            .OrderBy(w => w.Priority)
            .ThenByDescending(w => w.AddedAtUtc)
            .ToListAsync(ct);

        var results = new List<CustomerWishlistItemDto>(entities.Count);
        foreach (var item in entities)
        {
            var product = await catalogService.GetProductByIdAsync(item.ProductId, includeArchived: true, ct);
            results.Add(MapWishlistItemToDto(item, product));
        }

        return results;
    }

    public async Task<CustomerWishlistItemDto> AddProductToWishlistAsync(
        Guid customerId,
        Guid productId,
        string? note = null,
        int priority = 3,
        CancellationToken ct = default)
    {
        await RequireCustomerEntityAsync(customerId, ct);
        ValidateWishlistPriority(priority);

        var product = await RequireActiveCatalogProductAsync(productId, ct);

        var duplicate = await dbContext.WishlistItems
            .AnyAsync(w => w.CustomerId == customerId && w.ProductId == productId, ct);

        if (duplicate)
        {
            throw new CustomerDomainException(
                "DUPLICATE_WISHLIST_ITEM",
                $"Product '{product.Sku}' is already in customer {customerId}'s wishlist.");
        }

        var entity = new CustomerWishlistItemEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ProductId = product.Id,
            Sku = product.Sku,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Priority = priority,
            AddedAtUtc = DateTime.UtcNow
        };

        dbContext.WishlistItems.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        return MapWishlistItemToDto(entity, product);
    }

    public async Task<CustomerWishlistItemDto> UpdateWishlistItemAsync(
        Guid customerId,
        Guid productId,
        string? note,
        int priority,
        CancellationToken ct = default)
    {
        await RequireCustomerEntityAsync(customerId, ct);
        ValidateWishlistPriority(priority);

        var entity = await dbContext.WishlistItems
            .FirstOrDefaultAsync(w => w.CustomerId == customerId && w.ProductId == productId, ct);

        if (entity is null)
        {
            throw new CustomerDomainException(
                "WISHLIST_ITEM_NOT_FOUND",
                $"Product {productId} was not found in customer {customerId}'s wishlist.");
        }

        entity.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        entity.Priority = priority;
        await dbContext.SaveChangesAsync(ct);

        var product = await catalogService.GetProductByIdAsync(productId, includeArchived: true, ct);
        return MapWishlistItemToDto(entity, product);
    }

    public async Task<bool> RemoveProductFromWishlistAsync(
        Guid customerId,
        Guid productId,
        CancellationToken ct = default)
    {
        await RequireCustomerEntityAsync(customerId, ct);

        var entity = await dbContext.WishlistItems
            .FirstOrDefaultAsync(w => w.CustomerId == customerId && w.ProductId == productId, ct);

        if (entity is null)
        {
            throw new CustomerDomainException(
                "WISHLIST_ITEM_NOT_FOUND",
                $"Product {productId} was not found in customer {customerId}'s wishlist.");
        }

        dbContext.WishlistItems.Remove(entity);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    private async Task<CatalogProductDto> RequireActiveCatalogProductAsync(Guid productId, CancellationToken ct)
    {
        var product = await catalogService.GetProductByIdAsync(productId, includeArchived: true, ct);
        if (product is null)
        {
            throw new CustomerDomainException("PRODUCT_NOT_FOUND", $"Catalog product {productId} was not found.");
        }

        if (product.IsArchived)
        {
            throw new CustomerDomainException(
                "PRODUCT_ARCHIVED",
                $"Catalog product '{product.Sku}' is archived and cannot be added to favorites or wishlist.");
        }

        return product;
    }

    private static void ValidateWishlistPriority(int priority)
    {
        if (priority is < 1 or > 5)
        {
            throw new CustomerDomainException(
                "INVALID_WISHLIST_DATA",
                "Wishlist item priority must be between 1 (highest) and 5 (lowest).");
        }
    }

    private static CustomerWishlistItemDto MapWishlistItemToDto(
        CustomerWishlistItemEntity item,
        CatalogProductDto? product) =>
        new(
            Id: item.Id,
            CustomerId: item.CustomerId,
            ProductId: item.ProductId,
            Sku: product?.Sku ?? item.Sku,
            ProductName: product?.Name ?? item.Sku,
            Category: product?.Category ?? string.Empty,
            UnitPriceHt: product?.UnitPriceHt ?? 0m,
            AvailableStock: product?.AvailableStock ?? 0,
            IsOutOfStock: product?.IsOutOfStock ?? false,
            IsArchived: product?.IsArchived ?? false,
            Note: item.Note,
            Priority: item.Priority,
            AddedAtUtc: item.AddedAtUtc);

    // =========================================================================
    // 4. PRIVATE BUSINESS RULE VALIDATORS & HELPERS
    // =========================================================================

    public static void ValidateAndApplyCustomerRules(CustomerAccountDataModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Name = model.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            throw new CustomerDomainException("INVALID_CUSTOMER_DATA", "Customer name is required.");
        }

        model.SirenSiret = NormalizeSirenSiret(model.SirenSiret);
        if (string.IsNullOrWhiteSpace(model.SirenSiret) || model.SirenSiret.Length < 9)
        {
            throw new CustomerDomainException(
                "INVALID_CUSTOMER_DATA",
                "A valid SIREN (9 digits) or SIRET (14 digits) identifier is required.");
        }

        model.InvoicingAddress = model.InvoicingAddress?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model.InvoicingAddress))
        {
            throw new CustomerDomainException("INVALID_CUSTOMER_DATA", "Invoicing address is required.");
        }

        // Feature: if invoicing and delivery addresses are marked as the same, copy InvoicingAddress automatically
        if (model.UseInvoicingAddressForDelivery)
        {
            model.DeliveryAddress = model.InvoicingAddress;
        }
        else
        {
            model.DeliveryAddress = model.DeliveryAddress?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model.DeliveryAddress))
            {
                throw new CustomerDomainException(
                    "INVALID_CUSTOMER_DATA",
                    "Delivery address is required when UseInvoicingAddressForDelivery is false.");
            }

            if (string.Equals(model.InvoicingAddress, model.DeliveryAddress, StringComparison.OrdinalIgnoreCase))
            {
                model.UseInvoicingAddressForDelivery = true;
            }
        }

        // Business Rule: Must have at least a PhoneNumber OR a ContactEmail (or both) to contact the customer
        model.ContactEmail = string.IsNullOrWhiteSpace(model.ContactEmail) ? null : model.ContactEmail.Trim();
        model.PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber) ? null : model.PhoneNumber.Trim();

        if (model.ContactEmail is null && model.PhoneNumber is null)
        {
            throw new CustomerDomainException(
                "MISSING_CONTACT_METHOD",
                "A customer must have at least a phone number or an email address (or both) to be contactable.");
        }
    }

    public static void ValidateAndNormalizeTagModel(CustomTagDataModel tagModel)
    {
        ArgumentNullException.ThrowIfNull(tagModel);
        tagModel.Name = tagModel.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(tagModel.Name))
        {
            throw new CustomerDomainException("INVALID_TAG_DATA", "Custom tag name is required.");
        }

        tagModel.Value = string.IsNullOrWhiteSpace(tagModel.Value) ? null : tagModel.Value.Trim();
        tagModel.Color = string.IsNullOrWhiteSpace(tagModel.Color) ? "#7C3AED" : tagModel.Color.Trim();
    }

    public static string NormalizeSirenSiret(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private async Task<List<CustomTagEntity>> ResolveIndependentTagsForCustomerAsync(
        CustomerAccountDataModel customerModel,
        CancellationToken ct)
    {
        var resolved = new List<CustomTagEntity>();
        var seenTagIds = new HashSet<Guid>();

        // 1. Resolve pre-existing independent tags referenced by TagIds
        if (customerModel.TagIds.Count > 0)
        {
            var distinctIds = customerModel.TagIds.Where(id => id != Guid.Empty).Distinct().ToList();
            var existingById = await dbContext.CustomTags
                .Where(t => distinctIds.Contains(t.Id))
                .ToListAsync(ct);

            if (existingById.Count != distinctIds.Count)
            {
                var missingId = distinctIds.First(id => existingById.All(t => t.Id != id));
                throw new CustomerDomainException("TAG_NOT_FOUND", $"Custom tag {missingId} was not found.");
            }

            resolved.AddRange(existingById.Where(tagEntity => seenTagIds.Add(tagEntity.Id)));
        }

        // 2. Resolve or create independent tags passed inline in customerModel.Tags
        var seenNames = new HashSet<string>(
            resolved.Select(r => r.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var rawTag in customerModel.Tags)
        {
            var trimmedName = rawTag.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName) || !seenNames.Add(trimmedName))
            {
                continue;
            }

            var existingByName = await dbContext.CustomTags
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
                var newTagEntity = autoMapper.Map<CustomTagDataModel, CustomTagEntity>(rawTag);
                dbContext.CustomTags.Add(newTagEntity);
                seenTagIds.Add(newTagEntity.Id);
                resolved.Add(newTagEntity);
            }
        }

        return resolved;
    }

    private B2BCustomerDto MapCustomerEntityToDto(CustomerProfileEntity entity)
    {
        var dataModel = autoMapper.Map<CustomerProfileEntity, CustomerAccountDataModel>(entity);
        return autoMapper.Map<CustomerAccountDataModel, B2BCustomerDto>(dataModel);
    }

    private async Task<CustomerProfileEntity> RequireCustomerEntityAsync(Guid customerId, CancellationToken ct)
    {
        var customer = await dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);

        if (customer is null)
        {
            throw new CustomerDomainException(
                "CUSTOMER_NOT_FOUND",
                $"Customer {customerId} was not found.");
        }

        return customer;
    }

    private static bool MatchesCustomer(
        Guid? orderCustomerId,
        string? vatOrSiren,
        string? companyName,
        CustomerProfileEntity customer)
    {
        if (orderCustomerId.HasValue && orderCustomerId.Value == customer.Id)
        {
            return true;
        }

        var normalizedIdentifier = NormalizeSirenSiret(vatOrSiren);
        if (!string.IsNullOrEmpty(normalizedIdentifier) &&
            (string.Equals(normalizedIdentifier, customer.SirenSiret, StringComparison.OrdinalIgnoreCase) ||
             normalizedIdentifier.EndsWith(customer.SirenSiret, StringComparison.OrdinalIgnoreCase) ||
             customer.SirenSiret.EndsWith(normalizedIdentifier, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(companyName) &&
               string.Equals(companyName.Trim(), customer.Name, StringComparison.OrdinalIgnoreCase);
    }
}
