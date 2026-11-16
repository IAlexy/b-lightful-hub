namespace Ordering.Service;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Microsoft.EntityFrameworkCore;
using Ordering.Service.DataModels;
using Ordering.Service.Persistence;

// =========================================================================
// 1. CANCELLATION DATAMODELS & COMMANDS
// =========================================================================

public sealed class CancelOrderDataModel
{
    public Guid OrderId { get; set; }
    public string Reason { get; set; } = "Cancelled by B2B customer request";
}

public sealed class CancelOrderLineDataModel
{
    public Guid OrderId { get; set; }
    public Guid OrderLineId { get; set; }
    public string Reason { get; set; } = "Order line removed by B2B customer";
}

/// <summary>
/// Command to cancel an entire B2B Purchase Order.
/// Rejects cancellation if the order does not exist, is already cancelled, or is paid/partially paid.
/// </summary>
public sealed record CancelPurchaseOrderCommand(
    Guid OrderId,
    string? Reason = null) : ICommand<OrderCancellationResultDto>;

/// <summary>
/// Command to cancel/remove a specific Order Line from a B2B Purchase Order.
/// Rejects removal if the order or line does not exist, or if the order is paid/partially paid/cancelled.
/// Automatically cancels the parent order if its last remaining line is removed.
/// </summary>
public sealed record CancelOrderLineCommand(
    Guid OrderId,
    Guid OrderLineId,
    string? Reason = null) : ICommand<OrderCancellationResultDto>;

/// <summary>
/// Command to record a partial or full payment against an Order (updating PaidAmountHt and PaymentStatus).
/// </summary>
public sealed record RecordOrderPaymentCommand(
    Guid OrderId,
    decimal AmountPaidHt) : ICommand<PurchaseOrderDto>;

// =========================================================================
// 2. ORDER CANCELLATION DOMAIN SERVICE
// =========================================================================

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

// =========================================================================
// 3. CQRS COMMAND HANDLERS
// =========================================================================

public sealed class CancelPurchaseOrderCommandHandler(
    IOrderCancellationService cancellationService,
    IMapper autoMapper) : ICommandHandler<CancelPurchaseOrderCommand, OrderCancellationResultDto>
{
    public Task<OrderCancellationResultDto> HandleAsync(CancelPurchaseOrderCommand command, CancellationToken ct = default)
    {
        var cancelDataModel = autoMapper.Map<CancelPurchaseOrderCommand, CancelOrderDataModel>(command);
        return cancellationService.CancelOrderAsync(cancelDataModel, ct);
    }
}

public sealed class CancelOrderLineCommandHandler(
    IOrderCancellationService cancellationService,
    IMapper autoMapper) : ICommandHandler<CancelOrderLineCommand, OrderCancellationResultDto>
{
    public Task<OrderCancellationResultDto> HandleAsync(CancelOrderLineCommand command, CancellationToken ct = default)
    {
        var cancelLineDataModel = autoMapper.Map<CancelOrderLineCommand, CancelOrderLineDataModel>(command);
        return cancellationService.CancelOrderLineAsync(cancelLineDataModel, ct);
    }
}

public sealed class RecordOrderPaymentCommandHandler(
    IOrderCancellationService cancellationService) : ICommandHandler<RecordOrderPaymentCommand, PurchaseOrderDto>
{
    public Task<PurchaseOrderDto> HandleAsync(RecordOrderPaymentCommand command, CancellationToken ct = default) =>
        cancellationService.RecordOrderPaymentAsync(command.OrderId, command.AmountPaidHt, ct);
}
