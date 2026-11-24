namespace Catalog.Service.Handlers;

using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Messaging;

/// <summary>
/// Saga consumer: reserves stock in Catalog.Service whenever an order is confirmed in Ordering.Service.
/// </summary>
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

/// <summary>
/// Saga consumer: automatically releases reserved stock back to available stock when a confirmed order is cancelled.
/// </summary>
public sealed class OrderCancelledCatalogStockHandler(ISender sender)
    : IIntegrationEventHandler<OrderCancelledIntegrationEvent>
{
    public async Task HandleAsync(OrderCancelledIntegrationEvent integrationEvent, CancellationToken ct = default)
    {
        foreach (var line in integrationEvent.ReleasedLines)
        {
            await sender.SendCommandAsync<ReleaseCatalogStockCommand, CatalogProductDto?>(
                new ReleaseCatalogStockCommand(line.Sku, line.Quantity), ct);
        }
    }
}

/// <summary>
/// Saga consumer: automatically releases reserved stock back to available stock when a single line is cancelled on a confirmed order.
/// </summary>
public sealed class OrderLineCancelledCatalogStockHandler(ISender sender)
    : IIntegrationEventHandler<OrderLineCancelledIntegrationEvent>
{
    public async Task HandleAsync(OrderLineCancelledIntegrationEvent integrationEvent, CancellationToken ct = default)
    {
        await sender.SendCommandAsync<ReleaseCatalogStockCommand, CatalogProductDto?>(
            new ReleaseCatalogStockCommand(integrationEvent.ReleasedLine.Sku, integrationEvent.ReleasedLine.Quantity), ct);
    }
}
