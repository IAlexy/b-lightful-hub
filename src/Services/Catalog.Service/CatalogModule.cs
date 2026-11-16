namespace Catalog.Service;

using System.Collections.Concurrent;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using Microsoft.Extensions.DependencyInjection;

// ==========================================
// 1. DOMAIN ENTITY & DTOs
// ==========================================

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
    bool IsLowStock);

// ==========================================
// 2. STRONGLY-TYPED MAPPER PROFILE
// ==========================================

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
            IsLowStock: source.AvailableStock < 15);
    }
}

// ==========================================
// 3. IN-MEMORY MICROSERVICE STORE
// ==========================================

public sealed class CatalogDataStore
{
    public ConcurrentDictionary<string, CatalogProduct> ProductsBySku { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CatalogDataStore()
    {
        Seed();
    }

    private void Seed()
    {
        var initial = new[]
        {
            new CatalogProduct
            {
                Sku = "SRV-RACK-42U",
                Name = "Baie Serveur Haute Densité 42U refroidissement liquide",
                Category = "Datacenter Infrastructure",
                UnitPriceHt = 2400m,
                BulkTierMinQuantity = 5,
                BulkUnitPriceHt = 2040m,
                AvailableStock = 40,
                ReservedStock = 2,
                UnitWeightKg = 85.0
            },
            new CatalogProduct
            {
                Sku = "SW-FIBER-100G",
                Name = "Switch Fibre Optique 100GbE 32 Ports L3",
                Category = "Networking",
                UnitPriceHt = 4800m,
                BulkTierMinQuantity = 4,
                BulkUnitPriceHt = 4200m,
                AvailableStock = 28,
                ReservedStock = 0,
                UnitWeightKg = 9.5
            },
            new CatalogProduct
            {
                Sku = "UPS-LFP-10KVA",
                Name = "Onduleur Industriel LiFePO4 10kVA Double Conversion",
                Category = "Power & Energy",
                UnitPriceHt = 1950m,
                BulkTierMinQuantity = 6,
                BulkUnitPriceHt = 1650m,
                AvailableStock = 12,
                ReservedStock = 1,
                UnitWeightKg = 42.0
            }
        };

        foreach (var p in initial)
        {
            ProductsBySku[p.Sku] = p;
        }
    }
}

// ==========================================
// 4. CQRS COMMANDS, QUERIES & HANDLERS
// ==========================================

public sealed record CreateCatalogProductCommand(
    string Sku,
    string Name,
    string Category,
    decimal UnitPriceHt,
    int BulkTierMinQuantity,
    decimal BulkUnitPriceHt,
    int InitialStock,
    double UnitWeightKg) : ICommand<CatalogProductDto>;

public sealed class CreateCatalogProductCommandHandler(
    CatalogDataStore store,
    IB2BMapper mapper) : ICommandHandler<CreateCatalogProductCommand, CatalogProductDto>
{
    public Task<CatalogProductDto> HandleAsync(CreateCatalogProductCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Sku))
        {
            throw new ArgumentException("SKU is required.", nameof(command));
        }

        var product = new CatalogProduct
        {
            Sku = command.Sku.Trim().ToUpperInvariant(),
            Name = command.Name,
            Category = command.Category,
            UnitPriceHt = command.UnitPriceHt,
            BulkTierMinQuantity = command.BulkTierMinQuantity,
            BulkUnitPriceHt = command.BulkUnitPriceHt,
            AvailableStock = command.InitialStock,
            UnitWeightKg = command.UnitWeightKg
        };

        store.ProductsBySku[product.Sku] = product;
        return Task.FromResult(mapper.Map<CatalogProduct, CatalogProductDto>(product));
    }
}

public sealed record ReserveCatalogStockCommand(string Sku, int Quantity) : ICommand<CatalogProductDto?>;

public sealed class ReserveCatalogStockCommandHandler(
    CatalogDataStore store,
    IB2BMapper mapper) : ICommandHandler<ReserveCatalogStockCommand, CatalogProductDto?>
{
    public Task<CatalogProductDto?> HandleAsync(ReserveCatalogStockCommand command, CancellationToken ct = default)
    {
        if (!store.ProductsBySku.TryGetValue(command.Sku, out var product))
        {
            return Task.FromResult<CatalogProductDto?>(null);
        }

        var qty = Math.Max(0, command.Quantity);
        var reserved = Math.Min(product.AvailableStock, qty);
        product.AvailableStock -= reserved;
        product.ReservedStock += reserved;

        return Task.FromResult<CatalogProductDto?>(mapper.Map<CatalogProduct, CatalogProductDto>(product));
    }
}

public sealed record GetCatalogProductsQuery(string? Category = null) : IQuery<IReadOnlyList<CatalogProductDto>>;

public sealed class GetCatalogProductsQueryHandler(
    CatalogDataStore store,
    IB2BMapper mapper) : IQueryHandler<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>
{
    public Task<IReadOnlyList<CatalogProductDto>> HandleAsync(GetCatalogProductsQuery query, CancellationToken ct = default)
    {
        var items = store.ProductsBySku.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            items = items.Where(p => string.Equals(p.Category, query.Category, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = items.OrderBy(p => p.Sku).ToList();
        return Task.FromResult(mapper.MapList<CatalogProduct, CatalogProductDto>(ordered));
    }
}

public sealed record GetCatalogProductBySkuQuery(string Sku) : IQuery<CatalogProductDto?>;

public sealed class GetCatalogProductBySkuQueryHandler(
    CatalogDataStore store,
    IB2BMapper mapper) : IQueryHandler<GetCatalogProductBySkuQuery, CatalogProductDto?>
{
    public Task<CatalogProductDto?> HandleAsync(GetCatalogProductBySkuQuery query, CancellationToken ct = default)
    {
        if (store.ProductsBySku.TryGetValue(query.Sku, out var product))
        {
            return Task.FromResult<CatalogProductDto?>(mapper.Map<CatalogProduct, CatalogProductDto>(product));
        }

        return Task.FromResult<CatalogProductDto?>(null);
    }
}

// ==========================================
// 5. INTEGRATION EVENT HANDLER (SAGA)
// ==========================================

public sealed class OrderConfirmedCatalogStockHandler(ISender sender)
    : IIntegrationEventHandler<OrderConfirmedIntegrationEvent>
{
    public async Task HandleAsync(OrderConfirmedIntegrationEvent integrationEvent, CancellationToken ct = default)
    {
        foreach (var line in integrationEvent.Lines)
        {
            await sender.SendCommandAsync<ReserveCatalogStockCommand, CatalogProductDto?>(
                new ReserveCatalogStockCommand(line.Sku, line.Quantity), ct);
        }
    }
}

public static class CatalogServiceRegistration
{
    public static IServiceCollection AddCatalogMicroservice(this IServiceCollection services)
    {
        services.AddSingleton<CatalogDataStore>();
        services.AddSingleton<IMapProfile<CatalogProduct, CatalogProductDto>, CatalogProductToDtoProfile>();
        services.AddTransient<ICommandHandler<CreateCatalogProductCommand, CatalogProductDto>, CreateCatalogProductCommandHandler>();
        services.AddTransient<ICommandHandler<ReserveCatalogStockCommand, CatalogProductDto?>, ReserveCatalogStockCommandHandler>();
        services.AddTransient<IQueryHandler<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>, GetCatalogProductsQueryHandler>();
        services.AddTransient<IQueryHandler<GetCatalogProductBySkuQuery, CatalogProductDto?>, GetCatalogProductBySkuQueryHandler>();
        services.AddTransient<IIntegrationEventHandler<OrderConfirmedIntegrationEvent>, OrderConfirmedCatalogStockHandler>();
        return services;
    }
}
