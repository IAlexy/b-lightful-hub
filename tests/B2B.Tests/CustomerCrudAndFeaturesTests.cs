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
using Customer.Service.Services;
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
    // 1. AUTOMAPPER PROFILE & SERVICE VALIDATION TESTS (CONTACT RULE + ADDRESSES)
    // =========================================================================

    [Fact]
    public void AutoMapperAndService_EnforcesSameInvoicingAndDeliveryAddress_AndContactRule()
    {
        using var provider = BuildTestProvider();
        var mapper = provider.GetRequiredService<IMapper>();

        var command = new CreateB2BCustomerCommand(
            Name: "  Lumière Optique Industrielle SAS  ",
            SirenSiret: "812 345 678 00015",
            InvoicingAddress: "14 Avenue des Sciences, 69003 Lyon, France",
            DeliveryAddress: null,
            UseInvoicingAddressForDelivery: true,
            ContactEmail: null,
            PhoneNumber: "  +33 4 72 11 22 33  ",
            Tags:
            [
                new CustomTagInput("KeyAccount", "Optics Tier 1", "#10B981")
            ]);

        // 1. Pure structural mapping: Command -> CustomerAccountDataModel
        var dataModel = mapper.Map<CreateB2BCustomerCommand, CustomerAccountDataModel>(command);

        // 2. Service business rule application (same-address copy + contact rule)
        CustomerCrudService.ValidateAndApplyCustomerRules(dataModel);

        Assert.Equal("Lumière Optique Industrielle SAS", dataModel.Name);
        Assert.Equal("81234567800015", dataModel.SirenSiret);
        Assert.True(dataModel.UseInvoicingAddressForDelivery);
        Assert.Equal("14 Avenue des Sciences, 69003 Lyon, France", dataModel.InvoicingAddress);
        Assert.Equal("14 Avenue des Sciences, 69003 Lyon, France", dataModel.DeliveryAddress);
        Assert.Null(dataModel.ContactEmail);
        Assert.Equal("+33 4 72 11 22 33", dataModel.PhoneNumber);

        // 3. CustomerAccountDataModel -> CustomerProfileEntity -> CustomerAccountDataModel -> B2BCustomerDto
        var entity = mapper.Map<CustomerAccountDataModel, CustomerProfileEntity>(dataModel);
        var roundTripModel = mapper.Map<CustomerProfileEntity, CustomerAccountDataModel>(entity);
        var dto = mapper.Map<CustomerAccountDataModel, B2BCustomerDto>(roundTripModel);

        Assert.Equal(dataModel.Name, dto.Name);
        Assert.Equal(dataModel.SirenSiret, dto.SirenSiret);
        Assert.Equal(dataModel.InvoicingAddress, dto.DeliveryAddress);
        Assert.True(dto.UseInvoicingAddressForDelivery);
        Assert.Null(dto.ContactEmail);
        Assert.Equal("+33 4 72 11 22 33", dto.PhoneNumber);
    }

    [Fact]
    public async Task CustomerContactRule_AllowsPhoneOnly_EmailOnly_OrBoth_AndRejectsWhenBothAreMissing()
    {
        using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        // Case 1: Email ONLY (no PhoneNumber) -> Valid
        var emailOnly = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Email Only Corp",
                SirenSiret: "11122233300011",
                InvoicingAddress: "10 Rue Email, 75001 Paris",
                UseInvoicingAddressForDelivery: true,
                ContactEmail: "contact@email-only.fr",
                PhoneNumber: null));
        Assert.Equal("contact@email-only.fr", emailOnly.ContactEmail);
        Assert.Null(emailOnly.PhoneNumber);

        // Case 2: Phone ONLY (no ContactEmail) -> Valid
        var phoneOnly = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Phone Only Corp",
                SirenSiret: "22233344400022",
                InvoicingAddress: "20 Rue Téléphone, 69001 Lyon",
                UseInvoicingAddressForDelivery: true,
                ContactEmail: "   ",
                PhoneNumber: "+33 6 99 88 77 66"));
        Assert.Null(phoneOnly.ContactEmail);
        Assert.Equal("+33 6 99 88 77 66", phoneOnly.PhoneNumber);

        // Case 3: BOTH Email and Phone -> Valid
        var bothContacts = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Full Contact Corp",
                SirenSiret: "33344455500033",
                InvoicingAddress: "30 Rue Complète, 44000 Nantes",
                UseInvoicingAddressForDelivery: true,
                ContactEmail: "hello@full-contact.fr",
                PhoneNumber: "+33 2 40 00 11 22"));
        Assert.Equal("hello@full-contact.fr", bothContacts.ContactEmail);
        Assert.Equal("+33 2 40 00 11 22", bothContacts.PhoneNumber);

        // Case 4: NEITHER Email NOR Phone -> Throws MISSING_CONTACT_METHOD
        var missingEx = await Assert.ThrowsAsync<CustomerDomainException>(() =>
            sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
                new CreateB2BCustomerCommand(
                    Name: "Unreachable Ghost Corp",
                    SirenSiret: "44455566600044",
                    InvoicingAddress: "40 Rue Fantôme, 13001 Marseille",
                    UseInvoicingAddressForDelivery: true,
                    ContactEmail: null,
                    PhoneNumber: "   ")));

        Assert.Equal("MISSING_CONTACT_METHOD", missingEx.ErrorCode);
    }

    [Fact]
    public void CustomerService_ThrowsInvalidCustomerData_WhenDeliveryAddressMissingAndNotSameAsInvoicing()
    {
        var invalidModel = new CustomerAccountDataModel
        {
            Name = "Missing Delivery Corp",
            SirenSiret = "55210055400013",
            InvoicingAddress = "10 Rue de la Paix, 75002 Paris",
            DeliveryAddress = "   ",
            UseInvoicingAddressForDelivery = false,
            ContactEmail = "ops@missing-delivery.fr"
        };

        var ex = Assert.Throws<CustomerDomainException>(() => CustomerCrudService.ValidateAndApplyCustomerRules(invalidModel));
        Assert.Equal("INVALID_CUSTOMER_DATA", ex.ErrorCode);
    }

    // =========================================================================
    // 2. INDEPENDENT TAGS & 3-TABLE PERSISTENCE (B2BCustomers, CustomTags, CustomerTagAssignments)
    // =========================================================================

    [Fact]
    public async Task IndependentTagsAnd3TableAssociations_CreateTagsIndependently_AssignToMultipleCustomers_AndUnassignWithoutDeletingTag()
    {
        using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        // 1. Create 2 Custom Tags INDEPENDENTLY of any customer (stored in Table 2: "CustomTags")
        var vipTag = await sender.SendCommandAsync<CreateCustomTagCommand, CustomTagDto>(
            new CreateCustomTagCommand("VIP-Partner", "Priority 24/7 Support", "#7C3AED"));
        var aeroTag = await sender.SendCommandAsync<CreateCustomTagCommand, CustomTagDto>(
            new CreateCustomTagCommand("Aerospace", "EN9100 Certified", "#2563EB"));

        var allIndependentTags = await sender.SendQueryAsync<GetCustomTagsQuery, IReadOnlyList<CustomTagDto>>(
            new GetCustomTagsQuery());
        Assert.Equal(2, allIndependentTags.Count);

        // 2. Create Customer A referencing vipTag by TagId (creates row in Table 1 "B2BCustomers" + Table 3 "CustomerTagAssignments")
        var customerA = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Hexagone Aéronautique SA",
                SirenSiret: "380 129 866 00042",
                InvoicingAddress: "22 Boulevard de l'Aérospatiale, 31000 Toulouse",
                UseInvoicingAddressForDelivery: true,
                PhoneNumber: "+33 5 61 00 11 22",
                TagIds: [vipTag.Id]));

        Assert.Single(customerA.Tags);
        Assert.Equal(vipTag.Id, customerA.Tags[0].Id);

        // 3. Assign aeroTag to Customer A via AssignTagToCustomerCommand
        customerA = await sender.SendCommandAsync<AssignTagToCustomerCommand, B2BCustomerDto>(
            new AssignTagToCustomerCommand(customerA.Id, aeroTag.Id));
        Assert.Equal(2, customerA.Tags.Count);

        // 4. Create Customer B and attach the SAME vipTag & aeroTag (shared independent tags!)
        var customerB = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Dassault Systèmes Cloud SAS",
                SirenSiret: "322 306 440 00213",
                InvoicingAddress: "10 Rue Marcel Dassault, 78140 Vélizy",
                UseInvoicingAddressForDelivery: true,
                ContactEmail: "cloud@3ds.com",
                TagIds: [vipTag.Id, aeroTag.Id]));

        Assert.Equal(2, customerB.Tags.Count);

        // 5. Verify the 3 SQL tables in CustomerDbContext:
        //    - 2 rows in B2BCustomers
        //    - 2 rows in CustomTags
        //    - 4 rows in CustomerTagAssignments
        using (var db = provider.GetRequiredService<CustomerDbContext>())
        {
            Assert.Equal(2, await db.Customers.CountAsync());
            Assert.Equal(2, await db.CustomTags.CountAsync());
            Assert.Equal(4, await db.CustomerTagAssignments.CountAsync());
        }

        // 6. Remove vipTag from Customer A -> removes 1 row from CustomerTagAssignments,
        //    but keeps vipTag in CustomTags and still assigned to Customer B!
        customerA = await sender.SendCommandAsync<RemoveTagFromCustomerCommand, B2BCustomerDto>(
            new RemoveTagFromCustomerCommand(customerA.Id, vipTag.Id));
        Assert.Single(customerA.Tags);
        Assert.Equal("Aerospace", customerA.Tags[0].Name);

        var customerBAfterUnassign = await sender.SendQueryAsync<GetB2BCustomerByIdQuery, B2BCustomerDto?>(
            new GetB2BCustomerByIdQuery(customerB.Id));
        Assert.NotNull(customerBAfterUnassign);
        Assert.Equal(2, customerBAfterUnassign.Tags.Count);

        using (var db = provider.GetRequiredService<CustomerDbContext>())
        {
            Assert.Equal(2, await db.CustomTags.CountAsync());
            Assert.Equal(3, await db.CustomerTagAssignments.CountAsync());
        }

        // 7. Delete aeroTag from independent catalog -> cascades removal from CustomerTagAssignments for both Customer A & Customer B
        var tagDeleted = await sender.SendCommandAsync<DeleteCustomTagCommand, bool>(
            new DeleteCustomTagCommand(aeroTag.Id));
        Assert.True(tagDeleted);

        using (var db = provider.GetRequiredService<CustomerDbContext>())
        {
            Assert.Equal(1, await db.CustomTags.CountAsync());
            Assert.Equal(1, await db.CustomerTagAssignments.CountAsync());
        }
    }

    // =========================================================================
    // 3. CUSTOMER 360° QUERIES (ORDERS, INVOICES, DELIVERIES & FAVORITE PRODUCTS)
    // =========================================================================

    [Fact]
    public async Task CustomerCrossServiceQueries_RetrieveOrders_Invoices_Deliveries_AndCatalogBackedFavoriteProducts()
    {
        using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        // 1. Create a B2B Customer (with PhoneNumber only)
        var customer = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Nordic Quantum Compute AB",
                SirenSiret: "90123456700018",
                InvoicingAddress: "88 Pôle Scientifique, 59000 Lille",
                DeliveryAddress: "Quai Logistique B4, Port de Lille, 59000 Lille",
                UseInvoicingAddressForDelivery: false,
                PhoneNumber: "+33 3 20 55 66 77",
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

        // 4. Retrieve Catalog-backed Favorite Products -> returns ordered SKUs enriched with live Catalog data
        var favorites = await sender.SendQueryAsync<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>(
            new GetCustomerFavoriteProductsQuery(customer.Id));

        Assert.Equal(2, favorites.Count);
        var rackFav = Assert.Single(favorites, f => f.Sku == "SRV-RACK-42U");
        Assert.Equal(1, rackFav.OrderFrequency);
        Assert.Equal(5, rackFav.TotalUnitsOrdered);
        Assert.Equal("Datacenter Infrastructure", rackFav.Category);
        Assert.Equal(35, rackFav.AvailableStock);
    }

    // =========================================================================
    // 4. REST API & GRAPHQL INTEGRATION TESTS (WebApplicationFactory)
    // =========================================================================

    [Fact]
    public async Task CustomerRestAndGraphQLEndpoints_SupportIndependentTags_ContactRule_FullCrud_360Views_AndFavoriteProducts()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        // 1. Create an independent Custom Tag via POST /api/customers/tags
        var createTagResp = await client.PostAsJsonAsync("/api/customers/tags",
            new CreateCustomTagCommand("ColdChain-Cert", "Temperature Controlled", "#06B6D4"));
        Assert.Equal(HttpStatusCode.Created, createTagResp.StatusCode);
        var independentTag = await createTagResp.Content.ReadFromJsonAsync<CustomTagDto>();
        Assert.NotNull(independentTag);

        // 2. Verify POST /api/customers without Email or Phone returns 400 BadRequest (MISSING_CONTACT_METHOD)
        var noContactResp = await client.PostAsJsonAsync("/api/customers", new CreateB2BCustomerCommand(
            Name: "No Contact Biotech",
            SirenSiret: "777 888 999 00099",
            InvoicingAddress: "45 Quai des Chartrons, 33000 Bordeaux",
            UseInvoicingAddressForDelivery: true,
            ContactEmail: null,
            PhoneNumber: null));
        Assert.Equal(HttpStatusCode.BadRequest, noContactResp.StatusCode);
        var noContactJson = await noContactResp.Content.ReadAsStringAsync();
        Assert.Contains("MISSING_CONTACT_METHOD", noContactJson);

        // 3. POST /api/customers with PhoneNumber only + UseInvoicingAddressForDelivery = true
        var createResp = await client.PostAsJsonAsync("/api/customers", new CreateB2BCustomerCommand(
            Name: "Bordeaux Biotech Logistics SAS",
            SirenSiret: "777 888 999 00011",
            InvoicingAddress: "45 Quai des Chartrons, 33000 Bordeaux",
            DeliveryAddress: null,
            UseInvoicingAddressForDelivery: true,
            ContactEmail: null,
            PhoneNumber: "+33 5 56 12 34 56",
            Tags: [new CustomTagInput("KeyAccount", "Gold", "#F59E0B")]));

        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var createdCustomer = await createResp.Content.ReadFromJsonAsync<B2BCustomerDto>();
        Assert.NotNull(createdCustomer);
        Assert.Equal("45 Quai des Chartrons, 33000 Bordeaux", createdCustomer.DeliveryAddress);
        Assert.True(createdCustomer.UseInvoicingAddressForDelivery);
        Assert.Null(createdCustomer.ContactEmail);
        Assert.Equal("+33 5 56 12 34 56", createdCustomer.PhoneNumber);
        Assert.Single(createdCustomer.Tags);

        // 4. Assign the pre-created independentTag to this customer via POST /api/customers/{id}/tags/{tagId}
        var assignResp = await client.PostAsync($"/api/customers/{createdCustomer.Id}/tags/{independentTag.Id}", null);
        Assert.Equal(HttpStatusCode.OK, assignResp.StatusCode);
        var afterAssign = await assignResp.Content.ReadFromJsonAsync<B2BCustomerDto>();
        Assert.NotNull(afterAssign);
        Assert.Equal(2, afterAssign.Tags.Count);

        // 5. Create an Order for this customer via POST /api/ordering/orders
        var orderResp = await client.PostAsJsonAsync("/api/ordering/orders", new CreatePurchaseOrderCommand(
            BuyerCompanyName: createdCustomer.Name,
            BuyerVatNumber: createdCustomer.SirenSiret,
            PaymentTermsDays: 30,
            ShippingDestination: createdCustomer.DeliveryAddress,
            Items: [new OrderLineRequest("UPS-LI-10KVA", 4)],
            AutoConfirm: true));
        Assert.Equal(HttpStatusCode.Created, orderResp.StatusCode);

        // 6. GET /api/customers/{id}/orders, /invoices, /deliveries
        var orders = await client.GetFromJsonAsync<List<PurchaseOrderDto>>($"/api/customers/{createdCustomer.Id}/orders");
        var invoices = await client.GetFromJsonAsync<List<B2BInvoiceDto>>($"/api/customers/{createdCustomer.Id}/invoices");
        var deliveries = await client.GetFromJsonAsync<List<B2BShipmentDto>>($"/api/customers/{createdCustomer.Id}/deliveries");

        Assert.NotNull(orders);
        Assert.NotNull(invoices);
        Assert.NotNull(deliveries);
        Assert.Single(orders);
        Assert.Single(invoices);
        Assert.Single(deliveries);

        // 7. GET /api/customers/{id}/favorite-products -> 200 OK with Catalog-backed favorites
        var favResp = await client.GetAsync($"/api/customers/{createdCustomer.Id}/favorite-products");
        Assert.Equal(HttpStatusCode.OK, favResp.StatusCode);
        var favorites = await favResp.Content.ReadFromJsonAsync<List<CustomerFavoriteProductDto>>();
        Assert.NotNull(favorites);
        Assert.Single(favorites);
        Assert.Equal("UPS-LI-10KVA", favorites[0].Sku);
        Assert.Equal(1, favorites[0].OrderFrequency);
        Assert.Equal(4, favorites[0].TotalUnitsOrdered);

        // 8. Unassign the tag from the customer via DELETE /api/customers/{id}/tags/{tagId}
        var unassignResp = await client.DeleteAsync($"/api/customers/{createdCustomer.Id}/tags/{independentTag.Id}");
        Assert.Equal(HttpStatusCode.OK, unassignResp.StatusCode);
        var afterUnassign = await unassignResp.Content.ReadFromJsonAsync<B2BCustomerDto>();
        Assert.NotNull(afterUnassign);
        Assert.Single(afterUnassign.Tags);

        // Verify the independent tag still exists in GET /api/customers/tags/{tagId}
        var getTagResp = await client.GetAsync($"/api/customers/tags/{independentTag.Id}");
        Assert.Equal(HttpStatusCode.OK, getTagResp.StatusCode);

        // 9. PUT /api/customers/{id} & DELETE /api/customers/{id}
        var updateResp = await client.PutAsJsonAsync($"/api/customers/{createdCustomer.Id}", new UpdateB2BCustomerCommand(
            CustomerId: createdCustomer.Id,
            Name: "Bordeaux Biotech International SAS",
            SirenSiret: "77788899900011",
            InvoicingAddress: "45 Quai des Chartrons, 33000 Bordeaux",
            DeliveryAddress: "Zone Industrielle Mérignac Phare, 33700 Mérignac",
            UseInvoicingAddressForDelivery: false,
            ContactEmail: "ops@bordeaux-biotech.fr",
            PhoneNumber: "+33 5 56 12 34 56"));

        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updatedDto = await updateResp.Content.ReadFromJsonAsync<B2BCustomerDto>();
        Assert.NotNull(updatedDto);
        Assert.False(updatedDto.UseInvoicingAddressForDelivery);
        Assert.Equal("Zone Industrielle Mérignac Phare, 33700 Mérignac", updatedDto.DeliveryAddress);
        Assert.Equal("ops@bordeaux-biotech.fr", updatedDto.ContactEmail);

        var deleteResp = await client.DeleteAsync($"/api/customers/{createdCustomer.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        var getDeletedResp = await client.GetAsync($"/api/customers/{createdCustomer.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getDeletedResp.StatusCode);
    }

    [Fact]
    public async Task CustomerGraphQLSubgraph_SupportsIndependentTags_AssignTag_ContactRule_AndCustomerMutations()
    {
        using var provider = BuildTestProvider();
        var resolver = provider.GetRequiredService<CustomerGraphQLResolver>();

        // 1. Subgraph Mutation: createTag (independent tag)
        using var tagVarsDoc = JsonDocument.Parse("""{ "name": "Robotics-OEM", "value": "Industrial Arms", "color": "#8B5CF6" }""");
        var tagVars = tagVarsDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var tagResp = await resolver.ExecuteAsync(new CustomerGraphQLRequest(
            Query: "mutation ($name: String!, $value: String, $color: String) { createTag(name: $name, value: $value, color: $color) { id name } }",
            Variables: tagVars));
        Assert.Null(tagResp.Errors);
        var createdTag = Assert.IsType<CustomTagDto>(tagResp.Data!["createTag"]);

        // 2. Subgraph Mutation: createCustomer with phoneNumber only + UseInvoicingAddressForDelivery = true
        using var custVarsDoc = JsonDocument.Parse("""
        {
          "name": "Strasbourg Robotics GmbH",
          "sirenSiret": "411 222 333 00099",
          "invoicingAddress": "5 Parc d'Innovation, 67400 Illkirch",
          "useInvoicingAddressForDelivery": true,
          "phoneNumber": "+33 3 88 10 20 30"
        }
        """);
        var createVars = custVarsDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        var createResp = await resolver.ExecuteAsync(new CustomerGraphQLRequest(
            Query: "mutation ($name: String!, $sirenSiret: String!, $invoicingAddress: String!, $useInvoicingAddressForDelivery: Boolean, $phoneNumber: String) { createCustomer(name: $name, sirenSiret: $sirenSiret, invoicingAddress: $invoicingAddress, useInvoicingAddressForDelivery: $useInvoicingAddressForDelivery, phoneNumber: $phoneNumber) { id name deliveryAddress phoneNumber } }",
            Variables: createVars));

        Assert.Null(createResp.Errors);
        var createdDto = Assert.IsType<B2BCustomerDto>(createResp.Data!["createCustomer"]);
        Assert.Equal("5 Parc d'Innovation, 67400 Illkirch", createdDto.DeliveryAddress);
        Assert.Equal("+33 3 88 10 20 30", createdDto.PhoneNumber);
        Assert.Empty(createdDto.Tags);

        // 3. Subgraph Mutation: assignTagToCustomer
        using var assignVarsDoc = JsonDocument.Parse($$"""{ "customerId": "{{createdDto.Id}}", "tagId": "{{createdTag.Id}}" }""");
        var assignVars = assignVarsDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var assignResp = await resolver.ExecuteAsync(new CustomerGraphQLRequest(
            Query: "mutation ($customerId: ID!, $tagId: ID!) { assignTagToCustomer(customerId: $customerId, tagId: $tagId) { id tags { id name } } }",
            Variables: assignVars));
        Assert.Null(assignResp.Errors);
        var assignedCustomer = Assert.IsType<B2BCustomerDto>(assignResp.Data!["assignTagToCustomer"]);
        Assert.Single(assignedCustomer.Tags);
        Assert.Equal("Robotics-OEM", assignedCustomer.Tags[0].Name);
    }
}
