namespace Ordering.Service;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Messaging;
using Catalog.Service;
using Microsoft.EntityFrameworkCore;
using Ordering.Service.DataModels;
using Ordering.Service.Entities;
using Ordering.Service.Persistence;

public sealed class CreatePurchaseOrderCommandHandler(
    OrderingDbContext dbContext,
    OrderingSequenceGenerator sequenceGenerator,
    CatalogDataStore catalogStore,
    IEventBus eventBus,
    IMapper autoMapper) : ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderDto>
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

        // 1. Map Command -> Domain DataModels via AutoMapper
        var customerDataModel = autoMapper.Map<CreatePurchaseOrderCommand, CustomerDataModel>(command);
        var orderDataModel = autoMapper.Map<CreatePurchaseOrderCommand, OrderDataModel>(command);
        orderDataModel.OrderNumber = sequenceGenerator.NextOrderNumber();

        // 2. Enrich OrderLineDataModels with B2B Catalog tiered pricing & weight
        foreach (var line in orderDataModel.Lines)
        {
            line.OrderId = orderDataModel.Id;
            if (catalogStore.ProductsBySku.TryGetValue(line.Sku, out var catalogProduct))
            {
                line.ProductName = catalogProduct.Name;
                line.UnitPriceHt = catalogProduct.ComputeEffectiveUnitPrice(line.Quantity);
                line.UnitWeightKg = catalogProduct.UnitWeightKg;
            }
            else
            {
                line.ProductName = $"Custom B2B Item ({line.Sku})";
                line.UnitPriceHt = 500m;
                line.UnitWeightKg = 5.0;
            }
        }

        // 3. Resolve existing CustomerEntity by VAT number or map & insert a new CustomerEntity in EF Core
        var existingCustomer = await dbContext.Customers
            .FirstOrDefaultAsync(c => c.VatNumber == customerDataModel.VatNumber, ct);

        if (existingCustomer is null)
        {
            existingCustomer = autoMapper.Map<CustomerDataModel, CustomerEntity>(customerDataModel);
            dbContext.Customers.Add(existingCustomer);
        }

        orderDataModel.CustomerId = existingCustomer.Id;
        orderDataModel.Customer = autoMapper.Map<CustomerEntity, CustomerDataModel>(existingCustomer);

        // 4. Map OrderDataModel -> OrderEntity (including OrderLineEntities) via AutoMapper & persist with EF Core
        var orderEntity = autoMapper.Map<OrderDataModel, OrderEntity>(orderDataModel);
        orderEntity.CustomerId = existingCustomer.Id;
        orderEntity.Customer = existingCustomer;
        foreach (var lineEntity in orderEntity.Lines)
        {
            lineEntity.OrderId = orderEntity.Id;
        }

        dbContext.Orders.Add(orderEntity);
        await dbContext.SaveChangesAsync(ct);

        // 5. Publish integration event when Order is confirmed
        if (orderDataModel.Status == PurchaseOrderStatus.Confirmed)
        {
            var eventLines = orderDataModel.Lines
                .Select(l => new OrderLineItemEventPayload(l.Sku, l.ProductName, l.Quantity, l.UnitPriceHt, l.UnitWeightKg))
                .ToList();

            await eventBus.PublishAsync(new OrderConfirmedIntegrationEvent(
                Guid.NewGuid(),
                DateTime.UtcNow,
                orderEntity.Id,
                orderEntity.OrderNumber,
                existingCustomer.CompanyName,
                existingCustomer.VatNumber,
                orderEntity.PaymentTermsDays,
                orderEntity.ShippingDestination,
                orderEntity.TotalAmountHt,
                eventLines), ct);
        }

        // 6. Map persisted SQL Entity -> DataModel -> DTO via AutoMapper
        var persistedDataModel = autoMapper.Map<OrderEntity, OrderDataModel>(orderEntity);
        return autoMapper.Map<OrderDataModel, PurchaseOrderDto>(persistedDataModel);
    }
}

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
