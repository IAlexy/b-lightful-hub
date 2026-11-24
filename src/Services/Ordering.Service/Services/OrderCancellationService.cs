namespace Ordering.Service;

using AutoMapper;
using B2B.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;
using Ordering.Service.DataModels;
using Ordering.Service.Persistence;

public interface IOrderCancellationService
{
    Task<OrderCancellationResultDto> CancelOrderAsync(CancelOrderDataModel cancelModel, CancellationToken ct = default);
    Task<OrderCancellationResultDto> CancelOrderLineAsync(CancelOrderLineDataModel cancelLineModel, CancellationToken ct = default);
    Task<PurchaseOrderDto> RecordOrderPaymentAsync(Guid orderId, decimal amountPaidHt, CancellationToken ct = default);
}

public sealed class OrderCancellationService(
    OrderingDbContext dbContext,
    IEventBus eventBus,
    IMapper autoMapper) : IOrderCancellationService
{
    public async Task<OrderCancellationResultDto> CancelOrderAsync(
        CancelOrderDataModel cancelModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cancelModel);

        var orderEntity = await dbContext.Orders
            .Include(o => o.Customer)
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == cancelModel.OrderId, ct);

        if (orderEntity is null)
        {
            throw new OrderCancellationException(
                "ORDER_NOT_FOUND",
                $"Purchase order {cancelModel.OrderId} was not found.");
        }

        // Capture confirmation status and lines BEFORE cancellation so we can release reserved Catalog stock
        var wasConfirmed = string.Equals(orderEntity.Status, PurchaseOrderStatus.Confirmed.ToString(), StringComparison.OrdinalIgnoreCase);
        var releasedLines = orderEntity.Lines
            .Select(l => new OrderLineItemEventPayload(l.Sku, l.ProductName, l.Quantity, l.UnitPriceHt, l.UnitWeightKg))
            .ToList();

        // Map SQL Entity -> Domain DataModel via AutoMapper & enforce cancellation business rules
        var orderDataModel = autoMapper.Map<Ordering.Service.Entities.OrderEntity, OrderDataModel>(orderEntity);
        orderDataModel.ApplyCancellation(cancelModel.Reason);

        // Apply state changes to tracked SQL Entity and persist via EF Core
        orderEntity.Status = orderDataModel.Status.ToString();
        orderEntity.CancellationReason = orderDataModel.CancellationReason;
        orderEntity.CancelledAtUtc = orderDataModel.CancelledAtUtc;
        orderEntity.TotalAmountHt = 0m;

        await dbContext.SaveChangesAsync(ct);

        if (wasConfirmed && releasedLines.Count > 0)
        {
            await eventBus.PublishAsync(new OrderCancelledIntegrationEvent(
                EventId: Guid.NewGuid(),
                OccurredOnUtc: DateTime.UtcNow,
                OrderId: orderEntity.Id,
                OrderNumber: orderEntity.OrderNumber,
                Reason: orderDataModel.CancellationReason ?? cancelModel.Reason,
                ReleasedLines: releasedLines), ct);
        }

        return autoMapper.Map<OrderDataModel, OrderCancellationResultDto>(orderDataModel);
    }

    public async Task<OrderCancellationResultDto> CancelOrderLineAsync(
        CancelOrderLineDataModel cancelLineModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cancelLineModel);

        var orderEntity = await dbContext.Orders
            .Include(o => o.Customer)
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == cancelLineModel.OrderId, ct);

        if (orderEntity is null)
        {
            throw new OrderCancellationException(
                "ORDER_NOT_FOUND",
                $"Purchase order {cancelLineModel.OrderId} was not found.");
        }

        var wasConfirmed = string.Equals(orderEntity.Status, PurchaseOrderStatus.Confirmed.ToString(), StringComparison.OrdinalIgnoreCase);

        // Map SQL Entity -> Domain DataModel via AutoMapper & enforce line-removal rules
        var orderDataModel = autoMapper.Map<Ordering.Service.Entities.OrderEntity, OrderDataModel>(orderEntity);
        var removedLine = orderDataModel.RemoveOrderLine(cancelLineModel.OrderLineId, cancelLineModel.Reason);

        // Remove the line from EF Core DbSet and update parent Order totals/status
        var lineEntity = orderEntity.Lines.First(l => l.Id == removedLine.Id);
        var releasedLinePayload = new OrderLineItemEventPayload(
            lineEntity.Sku,
            lineEntity.ProductName,
            lineEntity.Quantity,
            lineEntity.UnitPriceHt,
            lineEntity.UnitWeightKg);

        orderEntity.Lines.Remove(lineEntity);
        dbContext.OrderLines.Remove(lineEntity);

        orderEntity.Status = orderDataModel.Status.ToString();
        orderEntity.TotalAmountHt = orderDataModel.TotalAmountHt;
        orderEntity.CancellationReason = orderDataModel.CancellationReason;
        orderEntity.CancelledAtUtc = orderDataModel.CancelledAtUtc;

        await dbContext.SaveChangesAsync(ct);

        if (wasConfirmed)
        {
            await eventBus.PublishAsync(new OrderLineCancelledIntegrationEvent(
                EventId: Guid.NewGuid(),
                OccurredOnUtc: DateTime.UtcNow,
                OrderId: orderEntity.Id,
                OrderNumber: orderEntity.OrderNumber,
                OrderLineId: removedLine.Id,
                Reason: cancelLineModel.Reason,
                ReleasedLine: releasedLinePayload), ct);
        }

        var resultDto = autoMapper.Map<OrderDataModel, OrderCancellationResultDto>(orderDataModel);
        return resultDto with { CancelledOrderLineId = removedLine.Id };
    }

    public async Task<PurchaseOrderDto> RecordOrderPaymentAsync(
        Guid orderId,
        decimal amountPaidHt,
        CancellationToken ct = default)
    {
        if (amountPaidHt <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountPaidHt), "Payment amount must be strictly positive.");
        }

        var orderEntity = await dbContext.Orders
            .Include(o => o.Customer)
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (orderEntity is null)
        {
            throw new OrderCancellationException("ORDER_NOT_FOUND", $"Purchase order {orderId} was not found.");
        }

        if (string.Equals(orderEntity.Status, PurchaseOrderStatus.Cancelled.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderCancellationException("ORDER_ALREADY_CANCELLED", $"Cannot record payment on cancelled order {orderEntity.OrderNumber}.");
        }

        orderEntity.PaidAmountHt = Math.Round(orderEntity.PaidAmountHt + amountPaidHt, 2);
        orderEntity.PaymentStatus = orderEntity.PaidAmountHt >= orderEntity.TotalAmountHt
            ? OrderPaymentStatus.Paid.ToString()
            : OrderPaymentStatus.PartiallyPaid.ToString();

        await dbContext.SaveChangesAsync(ct);

        var dataModel = autoMapper.Map<Ordering.Service.Entities.OrderEntity, OrderDataModel>(orderEntity);
        return autoMapper.Map<OrderDataModel, PurchaseOrderDto>(dataModel);
    }
}
