namespace Catalog.Service.Mapping;

using AutoMapper;
using Catalog.Service.DataModels;
using Catalog.Service.Entities;

/// <summary>
/// AutoMapper Profile performing pure structural mappings across:
/// Commands / Queries -> DataModels <-> EF Core SQL Entities -> DTOs.
/// </summary>
public sealed class CatalogAutoMapperProfile : Profile
{
    public CatalogAutoMapperProfile()
    {
        // 1. Category mappings
        CreateMap<CreateCatalogCategoryCommand, CatalogCategoryDataModel>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.Description, opt => opt.MapFrom(s => s.Description));

        CreateMap<UpdateCatalogCategoryCommand, CatalogCategoryDataModel>()
            .ForMember(d => d.Id, opt => opt.MapFrom(s => s.CategoryId))
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.Description, opt => opt.MapFrom(s => s.Description));

        CreateMap<CatalogCategoryDataModel, CatalogCategoryEntity>()
            .ForMember(d => d.Products, opt => opt.Ignore());

        CreateMap<CatalogCategoryEntity, CatalogCategoryDataModel>()
            .ForMember(d => d.ProductCount, opt => opt.MapFrom(s => s.Products.Count(p => !p.IsArchived)));

        CreateMap<CatalogCategoryDataModel, CatalogCategoryDto>();

        // 2. Product Tag mappings (3-table independent tag pattern)
        CreateMap<ProductTagInput, ProductTagDataModel>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.Value, opt => opt.MapFrom(s => s.Value))
            .ForMember(d => d.Color, opt => opt.MapFrom(s => s.Color ?? "#0EA5E9"));

        CreateMap<CreateProductTagCommand, ProductTagDataModel>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.Value, opt => opt.MapFrom(s => s.Value))
            .ForMember(d => d.Color, opt => opt.MapFrom(s => s.Color ?? "#0EA5E9"));

        CreateMap<ProductTagDataModel, ProductTagEntity>()
            .ForMember(d => d.ProductAssignments, opt => opt.Ignore());
        CreateMap<ProductTagEntity, ProductTagDataModel>();
        CreateMap<ProductTagDataModel, ProductTagDto>();

        CreateMap<AssignTagToProductCommand, ProductTagAssignmentDataModel>();
        CreateMap<RemoveTagFromProductCommand, ProductTagAssignmentDataModel>();
        CreateMap<ProductTagAssignmentDataModel, ProductTagAssignmentEntity>();

        // 3. Volume Price Tier mappings
        CreateMap<VolumePriceTierInput, VolumePriceTierDataModel>();
        CreateMap<VolumePriceTierDataModel, ProductVolumePriceTierEntity>()
            .ForMember(d => d.Product, opt => opt.Ignore());
        CreateMap<ProductVolumePriceTierEntity, VolumePriceTierDataModel>();

        // 4. Product Create / Update Commands -> CatalogProductDataModel
        CreateMap<CreateCatalogProductCommand, CatalogProductDataModel>()
            .ForMember(d => d.Sku, opt => opt.MapFrom(s => s.Sku ?? string.Empty))
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.Description, opt => opt.MapFrom(s => s.Description))
            .ForMember(d => d.CategoryId, opt => opt.MapFrom(s => s.CategoryId ?? Guid.Empty))
            .ForMember(d => d.CategoryName, opt => opt.MapFrom(s => s.Category ?? string.Empty))
            .ForMember(d => d.AvailableStock, opt => opt.MapFrom(s => s.InitialStock))
            .ForMember(d => d.ReservedStock, opt => opt.MapFrom(_ => 0))
            .ForMember(d => d.LowStockThreshold, opt => opt.MapFrom(s => s.LowStockThreshold))
            .ForMember(d => d.VolumePriceTiers, opt => opt.MapFrom(s => s.VolumePriceTiers ?? Array.Empty<VolumePriceTierInput>()))
            .ForMember(d => d.TagIds, opt => opt.MapFrom(s => s.TagIds != null ? s.TagIds.ToList() : new List<Guid>()))
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags ?? Array.Empty<ProductTagInput>()));

        CreateMap<UpdateCatalogProductCommand, CatalogProductDataModel>()
            .ForMember(d => d.Id, opt => opt.MapFrom(s => s.ProductId))
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.Description, opt => opt.MapFrom(s => s.Description))
            .ForMember(d => d.CategoryId, opt => opt.MapFrom(s => s.CategoryId ?? Guid.Empty))
            .ForMember(d => d.CategoryName, opt => opt.MapFrom(s => s.Category ?? string.Empty))
            .ForMember(d => d.LowStockThreshold, opt => opt.MapFrom(s => s.LowStockThreshold))
            .ForMember(d => d.VolumePriceTiers, opt => opt.MapFrom(s => s.VolumePriceTiers ?? Array.Empty<VolumePriceTierInput>()))
            .ForMember(d => d.TagIds, opt => opt.MapFrom(s => s.TagIds != null ? s.TagIds.ToList() : new List<Guid>()))
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags ?? Array.Empty<ProductTagInput>()));

        // 5. Query Objects -> Query DataModels
        CreateMap<GetCatalogProductsQuery, CatalogProductFilterDataModel>();
        CreateMap<GetCatalogProductByIdQuery, CatalogProductIdLookupDataModel>();
        CreateMap<GetCatalogCategoryByIdQuery, CatalogCategoryIdLookupDataModel>();

        // 6. Product DataModel <-> EF Core Entity
        CreateMap<CatalogProductDataModel, CatalogProductEntity>()
            .ForMember(d => d.Category, opt => opt.Ignore())
            .ForMember(d => d.VolumePriceTiers, opt => opt.Ignore())
            .ForMember(d => d.TagAssignments, opt => opt.Ignore())
            .ForMember(d => d.StockRefillAlerts, opt => opt.Ignore());

        CreateMap<CatalogProductEntity, CatalogProductDataModel>()
            .ForMember(d => d.CategoryName, opt => opt.MapFrom(s => s.Category != null ? s.Category.Name : string.Empty))
            .ForMember(d => d.VolumePriceTiers, opt => opt.MapFrom(s =>
                s.VolumePriceTiers.OrderBy(t => t.MinQuantity).ToList()))
            .ForMember(d => d.TagIds, opt => opt.MapFrom(s =>
                s.TagAssignments.Select(a => a.TagId).ToList()))
            .ForMember(d => d.Tags, opt => opt.MapFrom(s =>
                s.TagAssignments.Where(a => a.Tag != null).Select(a => a.Tag).ToList()));

        // 7. Product DataModel -> Output DTO
        CreateMap<CatalogProductDataModel, CatalogProductDto>()
            .ConstructUsing((src, ctx) =>
            {
                var tiers = src.VolumePriceTiers
                    .OrderBy(t => t.MinQuantity)
                    .Select(t => new VolumePriceTierDto(
                        t.Id,
                        t.MinQuantity,
                        t.UnitPriceHt,
                        t.ComputeDiscountPercent(src.UnitPriceHt)))
                    .ToList();

                var tags = src.Tags
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t => ctx.Map<ProductTagDataModel, ProductTagDto>(t))
                    .ToList();

                return new CatalogProductDto(
                    Id: src.Id,
                    Sku: src.Sku,
                    Name: src.Name,
                    Category: src.CategoryName,
                    UnitPriceHt: src.UnitPriceHt,
                    BulkTierMinQuantity: src.BulkTierMinQuantity,
                    BulkUnitPriceHt: src.BulkUnitPriceHt,
                    BulkDiscountPercent: src.ComputeBulkDiscountPercent(),
                    AvailableStock: src.AvailableStock,
                    ReservedStock: src.ReservedStock,
                    UnitWeightKg: src.UnitWeightKg,
                    IsLowStock: src.IsLowStock,
                    CategoryId: src.CategoryId,
                    Description: src.Description,
                    LowStockThreshold: src.LowStockThreshold,
                    IsOutOfStock: src.IsOutOfStock,
                    IsArchived: src.IsArchived,
                    CanBeOrdered: src.CanBeOrdered,
                    VolumePriceTiers: tiers,
                    Tags: tags,
                    CreatedAtUtc: src.CreatedAtUtc,
                    UpdatedAtUtc: src.UpdatedAtUtc,
                    ArchivedAtUtc: src.ArchivedAtUtc);
            });

        // 8. Stock Refill Alert mappings
        CreateMap<StockRefillAlertEntity, StockRefillAlertDataModel>()
            .ForMember(d => d.Sku, opt => opt.MapFrom(s => s.Product != null ? s.Product.Sku : string.Empty))
            .ForMember(d => d.ProductName, opt => opt.MapFrom(s => s.Product != null ? s.Product.Name : string.Empty));

        CreateMap<StockRefillAlertDataModel, StockRefillAlertDto>();
    }
}
