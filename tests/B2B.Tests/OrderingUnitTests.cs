namespace B2B.Tests;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using Catalog.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Service;
using Ordering.Service.DataModels;
using Ordering.Service.Entities;
using Ordering.Service.Mapping;
using Ordering.Service.Persistence;

public class OrderingUnitTests
{
    private static ServiceProvider BuildIsolatedOrderingServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISender, CqrsDispatcher>();
        services.AddSingleton<IB2BMapper, B2BMapper>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddCatalogMicroservice();
        services.AddOrderingMicroservice();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void OrderingAutoMapperProfile_Should_Map_Command_DataModels_Entities_And_Dtos()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<OrderingAutoMapperProfile>());
        var mapper = config.CreateMapper();

        var command = new CreatePurchaseOrderCommand(
            BuyerCompanyName: "Bordeaux Tech Systems SAS",
            BuyerVatNumber: "FR3399887766",
            PaymentTermsDays: 45,
            ShippingDestination: "33000 Bordeaux, France",
            Items:
            [
                new OrderLineRequest("srV-rack-42u", 4),
                new OrderLineRequest("sw-fiber-100g", 2)
            ],
            AutoConfirm: false,
            ContactEmail: "orders@bordeaux-tech.fr");

        // 1. Command -> CustomerDataModel & OrderDataModel
        var customerDm = mapper.Map<CreatePurchaseOrderCommand, CustomerDataModel>(command);
        var orderDm = mapper.Map<CreatePurchaseOrderCommand, OrderDataModel>(command);
        orderDm.OrderNumber = "PO-2026-9001";
        orderDm.Customer = customerDm;
        orderDm.CustomerId = customerDm.Id;

        foreach (var line in orderDm.Lines)
        {
            line.ProductName = $"Product {line.Sku}";
            line.UnitPriceHt = 1000m;
            line.UnitWeightKg = 12.5;
        }

        Assert.Equal("Bordeaux Tech Systems SAS", customerDm.CompanyName);
        Assert.Equal("FR3399887766", customerDm.VatNumber);
        Assert.Equal("orders@bordeaux-tech.fr", customerDm.ContactEmail);
        Assert.Equal(PurchaseOrderStatus.Draft, orderDm.Status);
        Assert.Equal(2, orderDm.Lines.Count);
        Assert.Equal("SRV-RACK-42U", orderDm.Lines[0].Sku);
        Assert.Equal(6000m, orderDm.TotalAmountHt);
        Assert.Equal(6, orderDm.TotalUnits);

        // 2. DataModels -> SQL Entities (CustomerEntity, OrderEntity, OrderLineEntity)
        var customerEntity = mapper.Map<CustomerDataModel, CustomerEntity>(customerDm);
        var orderEntity = mapper.Map<OrderDataModel, OrderEntity>(orderDm);
        orderEntity.Customer = customerEntity;

        Assert.Equal("Draft", orderEntity.Status);
        Assert.Equal(6000m, orderEntity.TotalAmountHt);
        Assert.Equal(2, orderEntity.Lines.Count);

        // 3. SQL Entities -> DataModels -> DTOs (PurchaseOrderDto & OrderSummaryDto)
        var roundTripDm = mapper.Map<OrderEntity, OrderDataModel>(orderEntity);
        var detailDto = mapper.Map<OrderDataModel, PurchaseOrderDto>(roundTripDm);
        var summaryDto = mapper.Map<OrderDataModel, OrderSummaryDto>(roundTripDm);

        Assert.Equal("PO-2026-9001", detailDto.OrderNumber);
        Assert.Equal("Bordeaux Tech Systems SAS", detailDto.Customer.CompanyName);
        Assert.Equal("Draft", detailDto.Status);
        Assert.Equal(6000m, detailDto.TotalAmountHt);
        Assert.Equal(2, summaryDto.LineCount);
        Assert.Equal(6, summaryDto.TotalUnits);
    }

    [Fact]
    public async Task CreatePurchaseOrderCommandHandler_Should_Persist_Customer_Order_And_OrderLines_In_EfCore()
    {
        await using var provider = BuildIsolatedOrderingServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        var dbContext = provider.GetRequiredService<OrderingDbContext>();

        var command = new CreatePurchaseOrderCommand(
            BuyerCompanyName: "Nantes Atlantique Cloud SA",
            BuyerVatNumber: "FR4411223344",
            PaymentTermsDays: 30,
            ShippingDestination: "Quai de la Fosse, 44000 Nantes",
            Items:
            [
                new OrderLineRequest("SRV-RACK-42U", 5), // Bulk tier (>=5): 2040 EUR HT
                new OrderLineRequest("CUSTOM-CABLING-KIT", 3) // Custom fallback: 500 EUR HT
            ],
            AutoConfirm: true);

        var result = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(command);

        Assert.NotNull(result);
        Assert.Equal("Confirmed", result.Status);
        Assert.Equal("Nantes Atlantique Cloud SA", result.Customer.CompanyName);
        Assert.Equal("FR4411223344", result.Customer.VatNumber);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal((5 * 2040m) + (3 * 500m), result.TotalAmountHt);

        // Verify relational persistence in EF Core SQLite tables
        Assert.Equal(1, await dbContext.Customers.CountAsync());
        Assert.Equal(1, await dbContext.Orders.CountAsync());
        Assert.Equal(2, await dbContext.OrderLines.CountAsync());
    }

    [Fact]
    public async Task CreatePurchaseOrderCommandHandler_Should_Reuse_Existing_CustomerEntity_When_Same_VatNumber_Orders_Again()
    {
        await using var provider = BuildIsolatedOrderingServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        var dbContext = provider.GetRequiredService<OrderingDbContext>();

        var firstOrder = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Lille Data Systems",
                BuyerVatNumber: "FR5900112233",
                PaymentTermsDays: 30,
                ShippingDestination: "EuraTechnologies, 59000 Lille",
                Items: [new OrderLineRequest("SW-FIBER-100G", 1)]));

        var secondOrder = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Lille Data Systems",
                BuyerVatNumber: "FR5900112233",
                PaymentTermsDays: 60,
                ShippingDestination: "EuraTechnologies, 59000 Lille",
                Items: [new OrderLineRequest("UPS-LFP-10KVA", 2)]));

        // Both orders must reference the exact same CustomerId in the Customers SQL table
        Assert.Equal(firstOrder.CustomerId, secondOrder.CustomerId);
        Assert.Equal(1, await dbContext.Customers.CountAsync());
        Assert.Equal(2, await dbContext.Orders.CountAsync());
    }

    [Fact]
    public async Task CreatePurchaseOrderCommandHandler_Should_Throw_On_Invalid_Arguments()
    {
        await using var provider = BuildIsolatedOrderingServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
                new CreatePurchaseOrderCommand(
                    BuyerCompanyName: "",
                    BuyerVatNumber: "FR00",
                    PaymentTermsDays: 30,
                    ShippingDestination: "Paris",
                    Items: [new OrderLineRequest("SRV-RACK-42U", 1)])));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
                new CreatePurchaseOrderCommand(
                    BuyerCompanyName: "Valid Co",
                    BuyerVatNumber: "FR00",
                    PaymentTermsDays: 30,
                    ShippingDestination: "Paris",
                    Items: [])));
    }

    [Fact]
    public async Task GetPurchaseOrdersQuery_And_GetPurchaseOrderByIdQuery_Should_Return_Mapped_Dtos_From_Sql_Entities()
    {
        await using var provider = BuildIsolatedOrderingServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        var orderA = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Alpha B2B Corp",
                BuyerVatNumber: "FR1010101010",
                PaymentTermsDays: 30,
                ShippingDestination: "Toulouse Aerospace Valley",
                Items: [new OrderLineRequest("SRV-RACK-42U", 2)]));

        var orderB = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Beta Industrial SA",
                BuyerVatNumber: "FR2020202020",
                PaymentTermsDays: 45,
                ShippingDestination: "Strasbourg Port du Rhin",
                Items: [new OrderLineRequest("SW-FIBER-100G", 4)]));

        // 1. Query all orders
        var allOrders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(
            new GetPurchaseOrdersQuery());
        Assert.Equal(2, allOrders.Count);

        // 2. Query orders filtered by BuyerVatNumber
        var filteredOrders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(
            new GetPurchaseOrdersQuery(BuyerVatNumber: "FR2020202020"));
        Assert.Single(filteredOrders);
        Assert.Equal(orderB.Id, filteredOrders[0].Id);
        Assert.Equal("Beta Industrial SA", filteredOrders[0].Customer.CompanyName);

        // 3. Query specific order by ID
        var foundOrder = await sender.SendQueryAsync<GetPurchaseOrderByIdQuery, PurchaseOrderDto?>(
            new GetPurchaseOrderByIdQuery(orderA.Id));
        Assert.NotNull(foundOrder);
        Assert.Equal(orderA.OrderNumber, foundOrder.OrderNumber);
        Assert.Equal("Alpha B2B Corp", foundOrder.Customer.CompanyName);
        Assert.Single(foundOrder.Lines);

        // 4. Query non-existent order ID -> returns null
        var missingOrder = await sender.SendQueryAsync<GetPurchaseOrderByIdQuery, PurchaseOrderDto?>(
            new GetPurchaseOrderByIdQuery(Guid.NewGuid()));
        Assert.Null(missingOrder);
    }
}
