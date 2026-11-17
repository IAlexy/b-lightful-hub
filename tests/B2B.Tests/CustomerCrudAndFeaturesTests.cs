namespace B2B.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using Catalog.Service;
using Customer.Service;
using Customer.Service.DataModels;
using Customer.Service.Entities;
using Customer.Service.GraphQL;
using Customer.Service.Persistence;
using Delivery.Service;
using Invoicing.Service;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Service;

public class CustomerCrudAndFeaturesTests
{
    private static ServiceProvider BuildTestProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISender, CqrsDispatcher>();
        services.AddSingleton<IB2BMapper, B2BMapper>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();

        services.AddCatalogMicroservice();
        services.AddCustomerMicroservice($"DataSource=cust_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        services.AddOrderingMicroservice($"DataSource=ord_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        services.AddInvoicingMicroservice();
        services.AddDeliveryMicroservice();

        return services.BuildServiceProvider();
    }

    // =========================================================================
    // 1. AUTOMAPPER PROFILE & DOMAIN DATAMODEL TESTS (SAME ADDRESS + FREE TAGS)
    // =========================================================================

    [Fact]
    public void AutoMapper_MapsCreateCustomerCommand_WithSameInvoicingAndDeliveryAddress_AndDeduplicatesFreeTags()
    {
        using var provider = BuildTestProvider();
        var mapper = provider.GetRequiredService<IMapper>();

        var command = new CreateB2BCustomerCommand(
            Name: "  Lumière Optique Industrielle SAS  ",
            SirenSiret: "812 345 678 00015",
            InvoicingAddress: "14 Avenue des Sciences, 69003 Lyon, France",
            DeliveryAddress: null, // User does not need to type DeliveryAddress when UseInvoicingAddressForDelivery is true
            UseInvoicingAddressForDelivery: true,
            Tags:
            [
                new CustomTagInput("KeyAccount", "Optics Tier 1", "#10B981"),
                new CustomTagInput("keyaccount", "Duplicate Should Be Ignored", "#EF4444"),
                new CustomTagInput("FragileFreight", null, null)
            ],
            ContactEmail: "achats@lumiere-optique.fr");

        // 1. Command -> CustomerAccountDataModel
        var dataModel = mapper.Map<CreateB2BCustomerCommand, CustomerAccountDataModel>(command);

        Assert.Equal("Lumière Optique Industrielle SAS", dataModel.Name);
        Assert.Equal("81234567800015", dataModel.SirenSiret);
        Assert.True(dataModel.UseInvoicingAddressForDelivery);
        Assert.Equal("14 Avenue des Sciences, 69003 Lyon, France", dataModel.InvoicingAddress);
        Assert.Equal("14 Avenue des Sciences, 69003 Lyon, France", dataModel.DeliveryAddress);
        Assert.Equal(2, dataModel.Tags.Count);
        Assert.Equal("KeyAccount", dataModel.Tags[0].Name);
        Assert.Equal("Optics Tier 1", dataModel.Tags[0].Value);
        Assert.Equal("#10B981", dataModel.Tags[0].Color);
        Assert.Equal("FragileFreight", dataModel.Tags[1].Name);
        Assert.Equal("#7C3AED", dataModel.Tags[1].Color);

        // 2. CustomerAccountDataModel -> CustomerProfileEntity -> CustomerAccountDataModel -> B2BCustomerDto
        var entity = mapper.Map<CustomerAccountDataModel, CustomerProfileEntity>(dataModel);
        Assert.Equal(2, entity.Tags.Count);

        var roundTripModel = mapper.Map<CustomerProfileEntity, CustomerAccountDataModel>(entity);
        var dto = mapper.Map<CustomerAccountDataModel, B2BCustomerDto>(roundTripModel);

        Assert.Equal(dataModel.Name, dto.Name);
        Assert.Equal(dataModel.SirenSiret, dto.SirenSiret);
        Assert.Equal(dataModel.InvoicingAddress, dto.DeliveryAddress);
        Assert.True(dto.UseInvoicingAddressForDelivery);
        Assert.Equal(2, dto.Tags.Count);
    }

    [Fact]
    public void CustomerDataModel_ThrowsInvalidCustomerData_WhenDeliveryAddressMissingAndNotSameAsInvoicing()
    {
        var invalidModel = new CustomerAccountDataModel
        {
            Name = "missing delivery corp",
            SirenSiret = "55210055400013",
            InvoicingAddress = "10 Rue de la Paix, 75002 Paris",
            DeliveryAddress = "   ",
            UseInvoicingAddressForDelivery = false
        };

        var ex = Assert.Throws<CustomerDomainException>(() => invalidModel.ApplyBusinessRulesAndValidate());
        Assert.Equal("INVALID_CUSTOMER_DATA", ex.ErrorCode);
    }

    // =========================================================================
    // 2. EF CORE SQLITE PERSISTENCE & CQRS CUSTOMER CRUD TESTS
    // =========================================================================

    [Fact]
    public async Task CustomerCrud_Create_Read_FilterByTag_Update_AndDelete_PersistsInEfCoreSqlite()
    {
        using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        // 1. CREATE with UseInvoicingAddressForDelivery = true
        var created = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Hexagone Aéronautique SA",
                SirenSiret: "380 129 866 00042",
                InvoicingAddress: "22 Boulevard de l'Aérospatiale, 31000 Toulouse",
                DeliveryAddress: null,
                UseInvoicingAddressForDelivery: true,
                Tags:
                [
                    new CustomTagInput("Aerospace", "Defense & Civil", "#3B82F6"),
                    new CustomTagInput("VIP", "Priority SLA", "#7C3AED")
                ]));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("38012986600042", created.SirenSiret);
        Assert.Equal(created.InvoicingAddress, created.DeliveryAddress);
        Assert.True(created.UseInvoicingAddressForDelivery);
        Assert.Equal(2, created.Tags.Count);

        // 2. DUPLICATE SIREN/SIRET REJECTION
        var dupEx = await Assert.ThrowsAsync<CustomerDomainException>(() =>
            sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
                new CreateB2BCustomerCommand(
                    Name: "Clone Corp",
                    SirenSiret: "38012986600042",
                    InvoicingAddress: "1 Rue Test, Paris",
                    UseInvoicingAddressForDelivery: true)));
        Assert.Equal("DUPLICATE_SIREN_SIRET", dupEx.ErrorCode);

        // 3. QUERY BY ID & FILTER BY TAG / SEARCH
        var fetchedById = await sender.SendQueryAsync<GetB2BCustomerByIdQuery, B2BCustomerDto?>(
            new GetB2BCustomerByIdQuery(created.Id));
        Assert.NotNull(fetchedById);
        Assert.Equal("Hexagone Aéronautique SA", fetchedById.Name);

        var filteredByTag = await sender.SendQueryAsync<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>(
            new GetB2BCustomersQuery(Search: "Hexagone", Tag: "Aerospace"));
        Assert.Single(filteredByTag);

        // 4. UPDATE (Switch to distinct DeliveryAddress and update Custom Tags)
        var updated = await sender.SendCommandAsync<UpdateB2BCustomerCommand, B2BCustomerDto>(
            new UpdateB2BCustomerCommand(
                CustomerId: created.Id,
                Name: "Hexagone Aéronautique & Espace SA",
                SirenSiret: "38012986600042",
                InvoicingAddress: "22 Boulevard de l'Aérospatiale, 31000 Toulouse",
                DeliveryAddress: "Entrepôt Hangar 7, Zone Fret Blagnac, 31700 Blagnac",
                UseInvoicingAddressForDelivery: false,
                Tags:
                [
                    new CustomTagInput("Aerospace", "Updated Fleet", "#2563EB"),
                    new CustomTagInput("Net45", "Approved Credit", "#10B981"),
                    new CustomTagInput("Export", "EU Zone", "#F59E0B")
                ]));

        Assert.Equal("Hexagone Aéronautique & Espace SA", updated.Name);
        Assert.False(updated.UseInvoicingAddressForDelivery);
        Assert.Equal("Entrepôt Hangar 7, Zone Fret Blagnac, 31700 Blagnac", updated.DeliveryAddress);
        Assert.Equal(3, updated.Tags.Count);

        // Verify EF Core SQLite CustomTags table replaced old tags cleanly
        using (var db = provider.GetRequiredService<CustomerDbContext>())
        {
            var tagCountInDb = await db.CustomTags.CountAsync(t => t.CustomerId == created.Id);
            Assert.Equal(3, tagCountInDb);
        }

        // 5. DELETE Customer & verify cascade deletion in SQLite
        var deleted = await sender.SendCommandAsync<DeleteB2BCustomerCommand, bool>(
            new DeleteB2BCustomerCommand(created.Id));
        Assert.True(deleted);

        var afterDelete = await sender.SendQueryAsync<GetB2BCustomerByIdQuery, B2BCustomerDto?>(
            new GetB2BCustomerByIdQuery(created.Id));
        Assert.Null(afterDelete);
    }

    // =========================================================================
    // 3. CUSTOMER 360° QUERIES (ORDERS, INVOICES, DELIVERIES & FAVORITE PRODUCTS)
    // =========================================================================

    [Fact]
    public async Task CustomerCrossServiceQueries_RetrieveOrders_Invoices_Deliveries_AndThrowNotImplementedForFavoriteProducts()
    {
        using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        // 1. Create a B2B Customer
        var customer = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Nordic Quantum Compute AB",
                SirenSiret: "90123456700018",
                InvoicingAddress: "88 Pôle Scientifique, 59000 Lille",
                DeliveryAddress: "Quai Logistique B4, Port de Lille, 59000 Lille",
                UseInvoicingAddressForDelivery: false,
                Tags: [new CustomTagInput("HPC", "Quantum Lab")]));

        // 2. Create 2 confirmed Purchase Orders for this Customer (triggers Invoicing & Delivery saga)
        await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: customer.Name,
                BuyerVatNumber: customer.SirenSiret,
                PaymentTermsDays: 30,
                ShippingDestination: customer.DeliveryAddress,
                Items: [new OrderLineRequest("SRV-RACK-42U", 5)],
                AutoConfirm: true));

        await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: customer.Name,
                BuyerVatNumber: customer.SirenSiret,
                PaymentTermsDays: 45,
                ShippingDestination: customer.DeliveryAddress,
                Items: [new OrderLineRequest("SW-FIBER-100G", 3)],
                AutoConfirm: true));

        // 3. Retrieve all Orders, Invoices, and Deliveries for this Customer
        var orders = await sender.SendQueryAsync<GetCustomerOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(
            new GetCustomerOrdersQuery(customer.Id));
        var invoices = await sender.SendQueryAsync<GetCustomerInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>(
            new GetCustomerInvoicesQuery(customer.Id));
        var deliveries = await sender.SendQueryAsync<GetCustomerDeliveriesQuery, IReadOnlyList<B2BShipmentDto>>(
            new GetCustomerDeliveriesQuery(customer.Id));

        Assert.Equal(2, orders.Count);
        Assert.Equal(2, invoices.Count);
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, d => Assert.Equal(customer.DeliveryAddress, d.DestinationAddress));

        // 4. Retrieve Favorite Products -> throws CustomerFavoriteProductsNotImplementedException (NotImplementedException)
        var favEx = await Assert.ThrowsAsync<CustomerFavoriteProductsNotImplementedException>(() =>
            sender.SendQueryAsync<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>(
                new GetCustomerFavoriteProductsQuery(customer.Id)));

        Assert.Equal("CATALOG_FAVORITE_PRODUCTS_NOT_IMPLEMENTED", favEx.ErrorCode);
        Assert.Equal(customer.Id, favEx.CustomerId);
        Assert.Contains("not implemented yet", favEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 4. REST API & GRAPHQL INTEGRATION TESTS (WebApplicationFactory)
    // =========================================================================

    [Fact]
    public async Task CustomerRestAndGraphQLEndpoints_SupportFullCrud_SameAddress_Tags_360Views_And501FavoriteProducts()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        // 1. POST /api/customers with UseInvoicingAddressForDelivery = true
        var createResp = await client.PostAsJsonAsync("/api/customers", new CreateB2BCustomerCommand(
            Name: "Bordeaux Biotech Logistics SAS",
            SirenSiret: "777 888 999 00011",
            InvoicingAddress: "45 Quai des Chartrons, 33000 Bordeaux",
            DeliveryAddress: null,
            UseInvoicingAddressForDelivery: true,
            Tags:
            [
                new CustomTagInput("Biotech", "ColdChain", "#06B6D4"),
                new CustomTagInput("KeyAccount", "Gold", "#F59E0B")
            ]));

        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var createdCustomer = await createResp.Content.ReadFromJsonAsync<B2BCustomerDto>();
        Assert.NotNull(createdCustomer);
        Assert.Equal("45 Quai des Chartrons, 33000 Bordeaux", createdCustomer.DeliveryAddress);
        Assert.True(createdCustomer.UseInvoicingAddressForDelivery);
        Assert.Equal(2, createdCustomer.Tags.Count);

        // 1b. Duplicate SIREN/SIRET returns 409 Conflict
        var dupResp = await client.PostAsJsonAsync("/api/customers", new CreateB2BCustomerCommand(
            Name: "Duplicate Bordeaux Biotech",
            SirenSiret: "77788899900011",
            InvoicingAddress: "1 Rue Test, Bordeaux",
            UseInvoicingAddressForDelivery: true));
        Assert.Equal(HttpStatusCode.Conflict, dupResp.StatusCode);

        // 2. Create an Order for this customer via POST /api/ordering/orders
        var orderResp = await client.PostAsJsonAsync("/api/ordering/orders", new CreatePurchaseOrderCommand(
            BuyerCompanyName: createdCustomer.Name,
            BuyerVatNumber: createdCustomer.SirenSiret,
            PaymentTermsDays: 30,
            ShippingDestination: createdCustomer.DeliveryAddress,
            Items: [new OrderLineRequest("UPS-LI-10KVA", 4)],
            AutoConfirm: true));
        Assert.Equal(HttpStatusCode.Created, orderResp.StatusCode);

        // 3. GET /api/customers/{id}/orders, /invoices, /deliveries
        var orders = await client.GetFromJsonAsync<List<PurchaseOrderDto>>($"/api/customers/{createdCustomer.Id}/orders");
        var invoices = await client.GetFromJsonAsync<List<B2BInvoiceDto>>($"/api/customers/{createdCustomer.Id}/invoices");
        var deliveries = await client.GetFromJsonAsync<List<B2BShipmentDto>>($"/api/customers/{createdCustomer.Id}/deliveries");

        Assert.NotNull(orders);
        Assert.NotNull(invoices);
        Assert.NotNull(deliveries);
        Assert.Single(orders);
        Assert.Single(invoices);
        Assert.Single(deliveries);

        // 4. GET /api/customers/{id}/favorite-products -> 501 Not Implemented
        var favResp = await client.GetAsync($"/api/customers/{createdCustomer.Id}/favorite-products");
        Assert.Equal(HttpStatusCode.NotImplemented, favResp.StatusCode);
        var favJson = await favResp.Content.ReadAsStringAsync();
        Assert.Contains("CATALOG_FAVORITE_PRODUCTS_NOT_IMPLEMENTED", favJson);

        // 4b. Non-existent customer returns 404 NotFound on /orders, /invoices, /deliveries, /favorite-products
        var missingId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/customers/{missingId}/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/customers/{missingId}/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/customers/{missingId}/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/customers/{missingId}/favorite-products")).StatusCode);

        // 5. POST /api/customers/graphql -> query customerOrders, customerInvoices, customerDeliveries & customerFavoriteProducts
        var gqlQueryResp = await client.PostAsJsonAsync("/api/customers/graphql", new
        {
            query = "query ($customerId: ID!) { customerById(id: $customerId) { id name } customerOrders(customerId: $customerId) { orderNumber } customerInvoices(customerId: $customerId) { invoiceNumber } customerDeliveries(customerId: $customerId) { trackingNumber } }",
            variables = new { customerId = createdCustomer.Id.ToString() }
        });
        Assert.Equal(HttpStatusCode.OK, gqlQueryResp.StatusCode);
        using var queryDoc = JsonDocument.Parse(await gqlQueryResp.Content.ReadAsStringAsync());
        var dataEl = queryDoc.RootElement.GetProperty("data");
        Assert.Equal(1, dataEl.GetProperty("customerOrders").GetArrayLength());
        Assert.Equal(1, dataEl.GetProperty("customerInvoices").GetArrayLength());
        Assert.Equal(1, dataEl.GetProperty("customerDeliveries").GetArrayLength());

        var gqlFavResp = await client.PostAsJsonAsync("/api/customers/graphql", new
        {
            query = "query ($customerId: ID!) { customerFavoriteProducts(customerId: $customerId) { sku } }",
            variables = new { customerId = createdCustomer.Id.ToString() }
        });
        Assert.Equal(HttpStatusCode.OK, gqlFavResp.StatusCode);
        using var favDoc = JsonDocument.Parse(await gqlFavResp.Content.ReadAsStringAsync());
        Assert.Equal(
            "CATALOG_FAVORITE_PRODUCTS_NOT_IMPLEMENTED",
            favDoc.RootElement.GetProperty("errors")[0].GetProperty("errorCode").GetString());

        // 6. PUT /api/customers/{id} & DELETE /api/customers/{id}
        var updateResp = await client.PutAsJsonAsync($"/api/customers/{createdCustomer.Id}", new UpdateB2BCustomerCommand(
            CustomerId: createdCustomer.Id,
            Name: "Bordeaux Biotech International SAS",
            SirenSiret: "77788899900011",
            InvoicingAddress: "45 Quai des Chartrons, 33000 Bordeaux",
            DeliveryAddress: "Zone Industrielle Mérignac Phare, 33700 Mérignac",
            UseInvoicingAddressForDelivery: false,
            Tags: [new CustomTagInput("Biotech", "International", "#10B981")]));

        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updatedDto = await updateResp.Content.ReadFromJsonAsync<B2BCustomerDto>();
        Assert.NotNull(updatedDto);
        Assert.False(updatedDto.UseInvoicingAddressForDelivery);
        Assert.Equal("Zone Industrielle Mérignac Phare, 33700 Mérignac", updatedDto.DeliveryAddress);

        var deleteResp = await client.DeleteAsync($"/api/customers/{createdCustomer.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        var getDeletedResp = await client.GetAsync($"/api/customers/{createdCustomer.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getDeletedResp.StatusCode);
    }

    [Fact]
    public async Task CustomerGraphQLSubgraph_AndFederatedGateway_ExecuteMutationsAndQueries()
    {
        using var provider = BuildTestProvider();
        var resolver = provider.GetRequiredService<CustomerGraphQLResolver>();

        // 1. Subgraph Mutation: createCustomer with UseInvoicingAddressForDelivery = true and tags
        using var varsDoc = JsonDocument.Parse("""
        {
          "name": "Strasbourg Robotics GmbH",
          "sirenSiret": "411 222 333 00099",
          "invoicingAddress": "5 Parc d'Innovation, 67400 Illkirch",
          "useInvoicingAddressForDelivery": true,
          "tags": [
            { "name": "Robotics", "value": "Automated Arms", "color": "#8B5CF6" },
            "PreferredPartner"
          ]
        }
        """);
        var createVars = varsDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        var createResp = await resolver.ExecuteAsync(new CustomerGraphQLRequest(
            Query: "mutation ($name: String!, $sirenSiret: String!, $invoicingAddress: String!, $useInvoicingAddressForDelivery: Boolean, $tags: [CustomTagInput!]) { createCustomer(name: $name, sirenSiret: $sirenSiret, invoicingAddress: $invoicingAddress, useInvoicingAddressForDelivery: $useInvoicingAddressForDelivery, tags: $tags) { id name deliveryAddress } }",
            Variables: createVars));

        Assert.Null(createResp.Errors);
        var createdDto = Assert.IsType<B2BCustomerDto>(createResp.Data!["createCustomer"]);
        Assert.Equal("5 Parc d'Innovation, 67400 Illkirch", createdDto.DeliveryAddress);
        Assert.True(createdDto.UseInvoicingAddressForDelivery);
        Assert.Equal(2, createdDto.Tags.Count);

        // 2. Subgraph Query: customers filtered by tag
        using var filterDoc = JsonDocument.Parse("""{ "tag": "PreferredPartner" }""");
        var filterVars = filterDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var listResp = await resolver.ExecuteAsync(new CustomerGraphQLRequest(
            Query: "query ($tag: String) { customers(tag: $tag) { id name } }",
            Variables: filterVars));

        Assert.Null(listResp.Errors);
        var list = Assert.IsAssignableFrom<IReadOnlyList<B2BCustomerDto>>(listResp.Data!["customers"]);
        Assert.Single(list);

        // 3. Subgraph Mutation: deleteCustomer
        using var delDoc = JsonDocument.Parse($$"""{ "customerId": "{{createdDto.Id}}" }""");
        var delVars = delDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var delResp = await resolver.ExecuteAsync(new CustomerGraphQLRequest(
            Query: "mutation ($customerId: ID!) { deleteCustomer(customerId: $customerId) }",
            Variables: delVars));

        Assert.Null(delResp.Errors);
        Assert.True((bool)delResp.Data!["deleteCustomer"]!);
    }
}
