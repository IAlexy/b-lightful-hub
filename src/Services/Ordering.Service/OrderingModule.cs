namespace Ordering.Service;

using System.Collections.Concurrent;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using Catalog.Service;
using Microsoft.Extensions.DependencyInjection;

public enum PurchaseOrderStatus
{
    Draft,
    Confirmed,
    Cancelled
}

public sealed class PurchaseOrderLine
{
    public required string Sku { get; init; }
    public required string ProductName { get; init; }
    public int Quantity { get; init; }
    public decimal UnitPriceHt { get; init; }
    public double UnitWeightKg { get; init; }
    public decimal LineTotalHt => Math.Round(Quantity * UnitPriceHt, 2);
}

public sealed class PurchaseOrder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string OrderNumber { get; init; }
    public required string BuyerCompanyName { get; init; }
    public required string BuyerVatNumber { get; init; }
    public int PaymentTermsDays { get; init; } = 30;
    public required string ShippingDestination { get; init; }
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public List<PurchaseOrderLine> Lines { get; init; } = [];
    public decimal TotalAmountHt => Lines.Sum(l => l.LineTotalHt);
}

public sealed record PurchaseOrderLineDto(
    string Sku,
    string ProductName,
    int Quantity,
    decimal UnitPriceHt,
    decimal LineTotalHt,
    double UnitWeightKg);

public sealed record PurchaseOrderDto(
    Guid Id,
    string OrderNumber,
    string BuyerCompanyName,
    string BuyerVatNumber,
    int PaymentTermsDays,
    string ShippingDestination,
    string Status,
    DateTime CreatedAtUtc,
    decimal TotalAmountHt,
    int TotalUnits,
    IReadOnlyList<PurchaseOrderLineDto> Lines);

public sealed class PurchaseOrderToDtoProfile : IMapProfile<PurchaseOrder, PurchaseOrderDto>
{
    public PurchaseOrderDto Map(PurchaseOrder source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lineDtos = source.Lines
            .Select(l => new PurchaseOrderLineDto(l.Sku, l.ProductName, l.Quantity, l.UnitPriceHt, l.LineTotalHt, l.UnitWeightKg))
            .ToList();

        return new PurchaseOrderDto(
            source.Id,
            source.OrderNumber,
            source.BuyerCompanyName,
            source.BuyerVatNumber,
            source.PaymentTermsDays,
            source.ShippingDestination,
            source.Status.ToString(),
            source.CreatedAtUtc,
            source.TotalAmountHt,
            source.Lines.Sum(l => l.Quantity),
            lineDtos);
    }
}

public sealed class OrderingDataStore
{
    private int _sequence = 1001;
    public ConcurrentDictionary<Guid, PurchaseOrder> Orders { get; } = new();

    public string NextOrderNumber() => $"PO-2026-{Interlocked.Increment(ref _sequence)}";
}

public sealed record OrderLineRequest(string Sku, int Quantity);

public sealed record CreatePurchaseOrderCommand(
    string BuyerCompanyName,
    string BuyerVatNumber,
    int PaymentTermsDays,
    string ShippingDestination,
    IReadOnlyList<OrderLineRequest> Items,
    bool AutoConfirm = true) : ICommand<PurchaseOrderDto>;

public sealed class CreatePurchaseOrderCommandHandler(
    OrderingDataStore store,
    CatalogDataStore catalogStore,
    ISender sender,
    IB2BMapper mapper) : ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderDto>
{
    public async Task<PurchaseOrderDto> HandleAsync(CreatePurchaseOrderCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.BuyerCompanyName))
        {
            throw new ArgumentException("BuyerCompanyName is required.", nameof(command));
        }

        if (command.Items is null || command.Items.Count == 0)
        {
            throw new ArgumentException("At least one order line is required.", nameof(command));
        }

        var order = new PurchaseOrder
        {
            OrderNumber = store.NextOrderNumber(),
            BuyerCompanyName = command.BuyerCompanyName,
            BuyerVatNumber = command.BuyerVatNumber,
            PaymentTermsDays = command.PaymentTermsDays <= 0 ? 30 : command.PaymentTermsDays,
            ShippingDestination = command.ShippingDestination
        };

        foreach (var req in command.Items)
        {
            var qty = Math.Max(1, req.Quantity);
            if (catalogStore.ProductsBySku.TryGetValue(req.Sku, out var catalogProduct))
            {
                order.Lines.Add(new PurchaseOrderLine
                {
                    Sku = catalogProduct.Sku,
                    ProductName = catalogProduct.Name,
                    Quantity = qty,
                    UnitPriceHt = catalogProduct.ComputeEffectiveUnitPrice(qty),
                    UnitWeightKg = catalogProduct.UnitWeightKg
                });
            }
            else
            {
                order.Lines.Add(new PurchaseOrderLine
                {
                    Sku = req.Sku.ToUpperInvariant(),
                    ProductName = $"Custom B2B Item ({req.Sku})",
                    Quantity = qty,
                    UnitPriceHt = 500m,
                    UnitWeightKg = 5.0
                });
            }
        }

        store.Orders[order.Id] = order;

        if (command.AutoConfirm)
        {
            return await sender.SendCommandAsync<ConfirmPurchaseOrderCommand, PurchaseOrderDto>(
                new ConfirmPurchaseOrderCommand(order.Id), ct);
        }

        return mapper.Map<PurchaseOrder, PurchaseOrderDto>(order);
    }
}

public sealed record ConfirmPurchaseOrderCommand(Guid OrderId) : ICommand<PurchaseOrderDto>;

public sealed class ConfirmPurchaseOrderCommandHandler(
    OrderingDataStore store,
    IEventBus eventBus,
    IB2BMapper mapper) : ICommandHandler<ConfirmPurchaseOrderCommand, PurchaseOrderDto>
{
    public async Task<PurchaseOrderDto> HandleAsync(ConfirmPurchaseOrderCommand command, CancellationToken ct = default)
    {
        if (!store.Orders.TryGetValue(command.OrderId, out var order))
        {
            throw new KeyNotFoundException($"PurchaseOrder {command.OrderId} not found.");
        }

        if (order.Status != PurchaseOrderStatus.Confirmed)
        {
            order.Status = PurchaseOrderStatus.Confirmed;

            var eventLines = order.Lines
                .Select(l => new OrderLineItemEventPayload(l.Sku, l.ProductName, l.Quantity, l.UnitPriceHt, l.UnitWeightKg))
                .ToList();

            await eventBus.PublishAsync(new OrderConfirmedIntegrationEvent(
                Guid.NewGuid(),
                DateTime.UtcNow,
                order.Id,
                order.OrderNumber,
                order.BuyerCompanyName,
                order.BuyerVatNumber,
                order.PaymentTermsDays,
                order.ShippingDestination,
                order.TotalAmountHt,
                eventLines), ct);
        }

        return mapper.Map<PurchaseOrder, PurchaseOrderDto>(order);
    }
}

public sealed record GetPurchaseOrdersQuery : IQuery<IReadOnlyList<PurchaseOrderDto>>;

public sealed class GetPurchaseOrdersQueryHandler(
    OrderingDataStore store,
    IB2BMapper mapper) : IQueryHandler<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>
{
    public Task<IReadOnlyList<PurchaseOrderDto>> HandleAsync(GetPurchaseOrdersQuery query, CancellationToken ct = default)
    {
        var orders = store.Orders.Values.OrderByDescending(o => o.CreatedAtUtc).ToList();
        return Task.FromResult(mapper.MapList<PurchaseOrder, PurchaseOrderDto>(orders));
    }
}

public sealed record GetPurchaseOrderByIdQuery(Guid OrderId) : IQuery<PurchaseOrderDto?>;

public sealed class GetPurchaseOrderByIdQueryHandler(
    OrderingDataStore store,
    IB2BMapper mapper) : IQueryHandler<GetPurchaseOrderByIdQuery, PurchaseOrderDto?>
{
    public Task<PurchaseOrderDto?> HandleAsync(GetPurchaseOrderByIdQuery query, CancellationToken ct = default)
    {
        if (store.Orders.TryGetValue(query.OrderId, out var order))
        {
            return Task.FromResult<PurchaseOrderDto?>(mapper.Map<PurchaseOrder, PurchaseOrderDto>(order));
        }

        return Task.FromResult<PurchaseOrderDto?>(null);
    }
}

public static class OrderingServiceRegistration
{
    public static IServiceCollection AddOrderingMicroservice(this IServiceCollection services)
    {
        services.AddSingleton<OrderingDataStore>();
        services.AddSingleton<IMapProfile<PurchaseOrder, PurchaseOrderDto>, PurchaseOrderToDtoProfile>();
        services.AddTransient<ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderDto>, CreatePurchaseOrderCommandHandler>();
        services.AddTransient<ICommandHandler<ConfirmPurchaseOrderCommand, PurchaseOrderDto>, ConfirmPurchaseOrderCommandHandler>();
        services.AddTransient<IQueryHandler<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>, GetPurchaseOrdersQueryHandler>();
        services.AddTransient<IQueryHandler<GetPurchaseOrderByIdQuery, PurchaseOrderDto?>, GetPurchaseOrderByIdQueryHandler>();
        return services;
    }
}
