namespace Ordering.Service;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Messaging;
using Catalog.Service.DataModels;
using Catalog.Service.Services;
using Microsoft.EntityFrameworkCore;
using Ordering.Service.DataModels;
using Ordering.Service.Entities;
using Ordering.Service.Persistence;

public sealed class CreatePurchaseOrderCommandHandler(
    OrderingDbContext dbContext,
    OrderingSequenceGenerator sequenceGenerator,
    ICatalogService catalogService,
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

        // 2. Enforce backend Catalog stock rules (anti-bypass: cannot order archived, out-of-stock, or > available stock)
        //    and enrich OrderLineDataModels with B2B Catalog multi-tier pricing & weight
        var aggregatedQtyBySku = orderDataModel.Lines
            .GroupBy(l => l.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity), StringComparer.OrdinalIgnoreCase);

        foreach (var line in orderDataModel.Lines)
        {
            if (line.Quantity <= 0)
            {
                throw new CatalogDomainException(
                    "INVALID_STOCK_OPERATION",
                    $"Order quantity for SKU '{line.Sku}' must be strictly positive.");
            }

            line.OrderId = orderDataModel.Id;
            var catalogProduct = await catalogService.GetProductBySkuAsync(line.Sku, includeArchived: true, ct);
            if (catalogProduct is not null)
            {
                if (catalogProduct.IsArchived)
                {
                    throw new CatalogDomainException(
                        "PRODUCT_ARCHIVED",
                        $"Cannot order archived product '{catalogProduct.Sku}'.");
                }

                if (catalogProduct.AvailableStock <= 0)
                {
                    throw new CatalogDomainException(
                        "OUT_OF_STOCK",
                        $"Cannot order '{catalogProduct.Sku}': product is currently out of stock.");
                }

                var totalRequestedForSku = aggregatedQtyBySku[line.Sku];
                if (totalRequestedForSku > catalogProduct.AvailableStock)
                {
                    throw new CatalogDomainException(
                        "INSUFFICIENT_STOCK",
                        $"Cannot order {totalRequestedForSku} unit(s) of '{catalogProduct.Sku}': only {catalogProduct.AvailableStock} available in stock.");
                }

                var effectivePrice = await catalogService.ComputeEffectiveUnitPriceAsync(line.Sku, line.Quantity, ct);
                line.ProductName = catalogProduct.Name;
                line.UnitPriceHt = effectivePrice ?? catalogProduct.UnitPriceHt;
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
