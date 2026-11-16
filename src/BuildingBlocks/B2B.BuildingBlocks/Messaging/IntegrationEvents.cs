namespace B2B.BuildingBlocks.Messaging;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTime OccurredOnUtc { get; }
}

public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken ct = default);
}

public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;

    IReadOnlyList<IIntegrationEvent> PublishedEvents { get; }
}

public sealed class InMemoryEventBus(IServiceProvider serviceProvider) : IEventBus
{
    private readonly ConcurrentQueue<IIntegrationEvent> _published = new();

    public IReadOnlyList<IIntegrationEvent> PublishedEvents => _published.ToArray();

    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        _published.Enqueue(integrationEvent);

        var handlers = serviceProvider.GetServices<IIntegrationEventHandler<TEvent>>();
        foreach (var handler in handlers)
        {
            await handler.HandleAsync(integrationEvent, ct);
        }
    }
}

public sealed record OrderLineItemEventPayload(
    string Sku,
    string ProductName,
    int Quantity,
    decimal UnitPriceHt,
    double UnitWeightKg);

/// <summary>
/// Published by Ordering.Service when a B2B Purchase Order is confirmed.
/// Consumed by:
/// - Catalog.Service (stock reservation)
/// - Invoicing.Service (automatic Net-30 B2B invoice creation)
/// - Delivery.Service (freight & pallet shipment scheduling)
/// </summary>
public sealed record OrderConfirmedIntegrationEvent(
    Guid EventId,
    DateTime OccurredOnUtc,
    Guid OrderId,
    string OrderNumber,
    string BuyerCompanyName,
    string BuyerVatNumber,
    int PaymentTermsDays,
    string ShippingDestination,
    decimal TotalAmountHt,
    IReadOnlyList<OrderLineItemEventPayload> Lines) : IIntegrationEvent;
