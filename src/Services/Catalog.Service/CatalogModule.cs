namespace Catalog.Service;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using B2B.BuildingBlocks.Notifications;
using Catalog.Service.GraphQL;
using Catalog.Service.Handlers;
using Catalog.Service.Mapping;
using Catalog.Service.Persistence;
using Catalog.Service.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Strongly-typed IB2BMapper profile preserved for backwards compatibility with B2BMapper tests.
/// </summary>
public sealed class CatalogProductToDtoProfile : IMapProfile<CatalogProduct, CatalogProductDto>
{
    public CatalogProductDto Map(CatalogProduct source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var discountPct = source.UnitPriceHt > 0 && source.BulkUnitPriceHt > 0
            ? Math.Round((1m - (source.BulkUnitPriceHt / source.UnitPriceHt)) * 100m, 1)
            : 0m;

        return new CatalogProductDto(
            source.Id,
            source.Sku,
            source.Name,
            source.Category,
            source.UnitPriceHt,
            source.BulkTierMinQuantity,
            source.BulkUnitPriceHt,
            discountPct,
            source.AvailableStock,
            source.ReservedStock,
            source.UnitWeightKg,
            IsLowStock: source.AvailableStock < 15,
            IsOutOfStock: source.AvailableStock <= 0,
            CanBeOrdered: source.AvailableStock > 0);
    }
}

public static class CatalogServiceRegistration
{
    public static IServiceCollection AddCatalogMicroservice(
        this IServiceCollection services,
        string? sqliteConnectionString = null)
    {
        // Ensure a shared email notification service exists even in isolated unit test containers
        services.TryAddSingleton<IEmailNotificationService, InMemoryEmailNotificationService>();

        // Register AutoMapper with CatalogAutoMapperProfile & legacy IB2BMapper profile
        services.AddAutoMapper(new CatalogAutoMapperProfile());
        services.AddSingleton<IMapProfile<CatalogProduct, CatalogProductDto>, CatalogProductToDtoProfile>();

        // Register EF Core CatalogDbContext backed by SQLite relational engine (6 tables)
        var connString = string.IsNullOrWhiteSpace(sqliteConnectionString)
            ? $"DataSource=catalog_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            : sqliteConnectionString;

        var keepAliveConnection = new SqliteConnection(connString);
        keepAliveConnection.Open();
        services.AddSingleton(keepAliveConnection);

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseSqlite(keepAliveConnection),
            contextLifetime: ServiceLifetime.Transient,
            optionsLifetime: ServiceLifetime.Singleton);

        // Ensure relational schema is created and default B2B catalog products/categories/tiers are seeded
        using (var tempProvider = services.BuildServiceProvider())
        using (var db = tempProvider.GetRequiredService<CatalogDbContext>())
        {
            db.Database.EnsureCreated();
            CatalogCrudService.SeedDefaultCatalog(db);
        }

        // Register Catalog Service
        services.AddTransient<ICatalogService, CatalogCrudService>();

        // Command Handlers
        services.AddTransient<ICommandHandler<CreateCatalogProductCommand, CatalogProductDto>, CreateCatalogProductCommandHandler>();
        services.AddTransient<ICommandHandler<UpdateCatalogProductCommand, CatalogProductDto>, UpdateCatalogProductCommandHandler>();
        services.AddTransient<ICommandHandler<ArchiveCatalogProductCommand, CatalogProductDto>, ArchiveCatalogProductCommandHandler>();
        services.AddTransient<ICommandHandler<UnarchiveCatalogProductCommand, CatalogProductDto>, UnarchiveCatalogProductCommandHandler>();
        services.AddTransient<ICommandHandler<CreateCatalogCategoryCommand, CatalogCategoryDto>, CreateCatalogCategoryCommandHandler>();
        services.AddTransient<ICommandHandler<UpdateCatalogCategoryCommand, CatalogCategoryDto>, UpdateCatalogCategoryCommandHandler>();
        services.AddTransient<ICommandHandler<DeleteCatalogCategoryCommand, bool>, DeleteCatalogCategoryCommandHandler>();
        services.AddTransient<ICommandHandler<CreateProductTagCommand, ProductTagDto>, CreateProductTagCommandHandler>();
        services.AddTransient<ICommandHandler<DeleteProductTagCommand, bool>, DeleteProductTagCommandHandler>();
        services.AddTransient<ICommandHandler<AssignTagToProductCommand, CatalogProductDto>, AssignTagToProductCommandHandler>();
        services.AddTransient<ICommandHandler<RemoveTagFromProductCommand, CatalogProductDto>, RemoveTagFromProductCommandHandler>();
        services.AddTransient<ICommandHandler<SetProductVolumePriceTiersCommand, CatalogProductDto>, SetProductVolumePriceTiersCommandHandler>();
        services.AddTransient<ICommandHandler<ReserveCatalogStockCommand, CatalogProductDto?>, ReserveCatalogStockCommandHandler>();
        services.AddTransient<ICommandHandler<ReleaseCatalogStockCommand, CatalogProductDto?>, ReleaseCatalogStockCommandHandler>();
        services.AddTransient<ICommandHandler<RestockCatalogProductCommand, RestockResultDto>, RestockCatalogProductCommandHandler>();
        services.AddTransient<ICommandHandler<AdjustCatalogStockCommand, RestockResultDto>, AdjustCatalogStockCommandHandler>();
        services.AddTransient<ICommandHandler<SubscribeStockRefillAlertCommand, StockRefillAlertDto>, SubscribeStockRefillAlertCommandHandler>();
        services.AddTransient<ICommandHandler<UnsubscribeStockRefillAlertCommand, bool>, UnsubscribeStockRefillAlertCommandHandler>();

        // Query Handlers
        services.AddTransient<IQueryHandler<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>, GetCatalogProductsQueryHandler>();
        services.AddTransient<IQueryHandler<GetCatalogProductByIdQuery, CatalogProductDto?>, GetCatalogProductByIdQueryHandler>();
        services.AddTransient<IQueryHandler<GetCatalogProductBySkuQuery, CatalogProductDto?>, GetCatalogProductBySkuQueryHandler>();
        services.AddTransient<IQueryHandler<ValidateProductAvailabilityQuery, ProductAvailabilityCheckDto>, ValidateProductAvailabilityQueryHandler>();
        services.AddTransient<IQueryHandler<GetCatalogCategoriesQuery, IReadOnlyList<CatalogCategoryDto>>, GetCatalogCategoriesQueryHandler>();
        services.AddTransient<IQueryHandler<GetCatalogCategoryByIdQuery, CatalogCategoryDto?>, GetCatalogCategoryByIdQueryHandler>();
        services.AddTransient<IQueryHandler<GetProductTagsQuery, IReadOnlyList<ProductTagDto>>, GetProductTagsQueryHandler>();
        services.AddTransient<IQueryHandler<GetProductTagByIdQuery, ProductTagDto?>, GetProductTagByIdQueryHandler>();
        services.AddTransient<IQueryHandler<GetProductRefillAlertsQuery, IReadOnlyList<StockRefillAlertDto>>, GetProductRefillAlertsQueryHandler>();

        // Integration Event Handlers (Saga: Reserve on Confirm, Release on Cancel/LineCancel)
        services.AddTransient<IIntegrationEventHandler<OrderConfirmedIntegrationEvent>, OrderConfirmedCatalogStockHandler>();
        services.AddTransient<IIntegrationEventHandler<OrderCancelledIntegrationEvent>, OrderCancelledCatalogStockHandler>();
        services.AddTransient<IIntegrationEventHandler<OrderLineCancelledIntegrationEvent>, OrderLineCancelledCatalogStockHandler>();

        // GraphQL Subgraph Resolver
        services.AddTransient<CatalogGraphQLResolver>();

        return services;
    }
}
