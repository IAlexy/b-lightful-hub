namespace Ordering.Service;

using AutoMapper;
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

        // Map SQL Entity -> Domain DataModel via AutoMapper & enforce cancellation business rules
        var orderDataModel = autoMapper.Map<Ordering.Service.Entities.OrderEntity, OrderDataModel>(orderEntity);
        orderDataModel.ApplyCancellation(cancelModel.Reason);

        // Apply state changes to tracked SQL Entity and persist via EF Core
        orderEntity.Status = orderDataModel.Status.ToString();
        orderEntity.CancellationReason = orderDataModel.CancellationReason;
        orderEntity.CancelledAtUtc = orderDataModel.CancelledAtUtc;
        orderEntity.TotalAmountHt = 0m;

        await dbContext.SaveChangesAsync(ct);

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

        // Map SQL Entity -> Domain DataModel via AutoMapper & enforce line-removal rules
        var orderDataModel = autoMapper.Map<Ordering.Service.Entities.OrderEntity, OrderDataModel>(orderEntity);
        var removedLine = orderDataModel.RemoveOrderLine(cancelLineModel.OrderLineId, cancelLineModel.Reason);

        // Remove the line from EF Core DbSet and update parent Order totals/status
        var lineEntity = orderEntity.Lines.First(l => l.Id == removedLine.Id);
        orderEntity.Lines.Remove(lineEntity);
        dbContext.OrderLines.Remove(lineEntity);

        orderEntity.Status = orderDataModel.Status.ToString();
        orderEntity.TotalAmountHt = orderDataModel.TotalAmountHt;
        orderEntity.CancellationReason = orderDataModel.CancellationReason;
        orderEntity.CancelledAtUtc = orderDataModel.CancelledAtUtc;

        await dbContext.SaveChangesAsync(ct);

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
