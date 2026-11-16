namespace Delivery.Service;

using System.Collections.Concurrent;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using Microsoft.Extensions.DependencyInjection;

public enum B2BShipmentStatus
{
    Preparing,
    InTransit,
    Delivered
}

public sealed class B2BShipment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TrackingNumber { get; init; }
    public required Guid OrderId { get; init; }
    public required string OrderNumber { get; init; }
    public required string CarrierName { get; init; }
    public required string DestinationAddress { get; init; }
    public double TotalWeightKg { get; init; }
    public int PalletCount => Math.Max(1, (int)Math.Ceiling(TotalWeightKg / 250.0));
    public B2BShipmentStatus Status { get; set; } = B2BShipmentStatus.Preparing;
    public DateTime EstimatedDeliveryUtc { get; init; }
    public DateTime? DeliveredAtUtc { get; set; }
}

public sealed record B2BShipmentDto(
    Guid Id,
    string TrackingNumber,
    Guid OrderId,
    string OrderNumber,
    string CarrierName,
    string DestinationAddress,
    double TotalWeightKg,
    int PalletCount,
    string Status,
    DateTime EstimatedDeliveryUtc,
    DateTime? DeliveredAtUtc);

public sealed class ShipmentToDtoProfile : IMapProfile<B2BShipment, B2BShipmentDto>
{
    public B2BShipmentDto Map(B2BShipment source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new B2BShipmentDto(
            source.Id,
            source.TrackingNumber,
            source.OrderId,
            source.OrderNumber,
            source.CarrierName,
            source.DestinationAddress,
            source.TotalWeightKg,
            source.PalletCount,
            source.Status.ToString(),
            source.EstimatedDeliveryUtc,
            source.DeliveredAtUtc);
    }
}

public sealed class DeliveryDataStore
{
    private int _seq = 8001;
    public ConcurrentDictionary<Guid, B2BShipment> Shipments { get; } = new();

    public string NextTrackingNumber() => $"FRT-EU-{Interlocked.Increment(ref _seq)}";
}

public sealed record ScheduleShipmentCommand(
    Guid OrderId,
    string OrderNumber,
    string DestinationAddress,
    double TotalWeightKg) : ICommand<B2BShipmentDto>;

public sealed class ScheduleShipmentCommandHandler(
    DeliveryDataStore store,
    IB2BMapper mapper) : ICommandHandler<ScheduleShipmentCommand, B2BShipmentDto>
{
    public Task<B2BShipmentDto> HandleAsync(ScheduleShipmentCommand command, CancellationToken ct = default)
    {
        var existing = store.Shipments.Values.FirstOrDefault(s => s.OrderId == command.OrderId);
        if (existing is not null)
        {
            return Task.FromResult(mapper.Map<B2BShipment, B2BShipmentDto>(existing));
        }

        var carrier = command.TotalWeightKg > 150.0 ? "DHL Global Freight B2B" : "Geodis Express Palette";
        var shipment = new B2BShipment
        {
            TrackingNumber = store.NextTrackingNumber(),
            OrderId = command.OrderId,
            OrderNumber = command.OrderNumber,
            CarrierName = carrier,
            DestinationAddress = command.DestinationAddress,
            TotalWeightKg = Math.Round(command.TotalWeightKg, 2),
            EstimatedDeliveryUtc = DateTime.UtcNow.AddDays(3)
        };

        store.Shipments[shipment.Id] = shipment;
        return Task.FromResult(mapper.Map<B2BShipment, B2BShipmentDto>(shipment));
    }
}

public sealed record AdvanceShipmentStatusCommand(Guid ShipmentId) : ICommand<B2BShipmentDto?>;

public sealed class AdvanceShipmentStatusCommandHandler(
    DeliveryDataStore store,
    IB2BMapper mapper) : ICommandHandler<AdvanceShipmentStatusCommand, B2BShipmentDto?>
{
    public Task<B2BShipmentDto?> HandleAsync(AdvanceShipmentStatusCommand command, CancellationToken ct = default)
    {
        if (!store.Shipments.TryGetValue(command.ShipmentId, out var shipment))
        {
            return Task.FromResult<B2BShipmentDto?>(null);
        }

        if (shipment.Status == B2BShipmentStatus.Preparing)
        {
            shipment.Status = B2BShipmentStatus.InTransit;
        }
        else if (shipment.Status == B2BShipmentStatus.InTransit)
        {
            shipment.Status = B2BShipmentStatus.Delivered;
            shipment.DeliveredAtUtc = DateTime.UtcNow;
        }

        return Task.FromResult<B2BShipmentDto?>(mapper.Map<B2BShipment, B2BShipmentDto>(shipment));
    }
}

public sealed record GetShipmentsQuery : IQuery<IReadOnlyList<B2BShipmentDto>>;

public sealed class GetShipmentsQueryHandler(
    DeliveryDataStore store,
    IB2BMapper mapper) : IQueryHandler<GetShipmentsQuery, IReadOnlyList<B2BShipmentDto>>
{
    public Task<IReadOnlyList<B2BShipmentDto>> HandleAsync(GetShipmentsQuery query, CancellationToken ct = default)
    {
        var list = store.Shipments.Values.OrderByDescending(s => s.EstimatedDeliveryUtc).ToList();
        return Task.FromResult(mapper.MapList<B2BShipment, B2BShipmentDto>(list));
    }
}

public sealed record GetShipmentByOrderIdQuery(Guid OrderId) : IQuery<B2BShipmentDto?>;

public sealed class GetShipmentByOrderIdQueryHandler(
    DeliveryDataStore store,
    IB2BMapper mapper) : IQueryHandler<GetShipmentByOrderIdQuery, B2BShipmentDto?>
{
    public Task<B2BShipmentDto?> HandleAsync(GetShipmentByOrderIdQuery query, CancellationToken ct = default)
    {
        var shipment = store.Shipments.Values.FirstOrDefault(s => s.OrderId == query.OrderId);
        return Task.FromResult(shipment is null ? null : mapper.Map<B2BShipment, B2BShipmentDto>(shipment));
    }
}

public sealed class OrderConfirmedShipmentHandler(ISender sender)
    : IIntegrationEventHandler<OrderConfirmedIntegrationEvent>
{
    public async Task HandleAsync(OrderConfirmedIntegrationEvent integrationEvent, CancellationToken ct = default)
    {
        var totalWeight = integrationEvent.Lines.Sum(l => l.Quantity * l.UnitWeightKg);
        await sender.SendCommandAsync<ScheduleShipmentCommand, B2BShipmentDto>(
            new ScheduleShipmentCommand(
                integrationEvent.OrderId,
                integrationEvent.OrderNumber,
                integrationEvent.ShippingDestination,
                totalWeight), ct);
    }
}

public static class DeliveryServiceRegistration
{
    public static IServiceCollection AddDeliveryMicroservice(this IServiceCollection services)
    {
        services.AddSingleton<DeliveryDataStore>();
        services.AddSingleton<IMapProfile<B2BShipment, B2BShipmentDto>, ShipmentToDtoProfile>();
        services.AddTransient<ICommandHandler<ScheduleShipmentCommand, B2BShipmentDto>, ScheduleShipmentCommandHandler>();
        services.AddTransient<ICommandHandler<AdvanceShipmentStatusCommand, B2BShipmentDto?>, AdvanceShipmentStatusCommandHandler>();
        services.AddTransient<IQueryHandler<GetShipmentsQuery, IReadOnlyList<B2BShipmentDto>>, GetShipmentsQueryHandler>();
        services.AddTransient<IQueryHandler<GetShipmentByOrderIdQuery, B2BShipmentDto?>, GetShipmentByOrderIdQueryHandler>();
        services.AddTransient<IIntegrationEventHandler<OrderConfirmedIntegrationEvent>, OrderConfirmedShipmentHandler>();
        return services;
    }
}
