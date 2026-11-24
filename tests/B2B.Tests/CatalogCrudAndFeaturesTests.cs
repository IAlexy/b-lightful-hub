namespace B2B.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using B2B.BuildingBlocks.Notifications;
using Catalog.Service;
using Catalog.Service.DataModels;
using Catalog.Service.Entities;
using Catalog.Service.GraphQL;
using Catalog.Service.Persistence;
using Catalog.Service.Services;
using Customer.Service;
using Customer.Service.DataModels;
using Customer.Service.GraphQL;
using Delivery.Service;
using Invoicing.Service;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Service;

public class CatalogCrudAndFeaturesTests
{
    private static ServiceProvider BuildTestProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISender, CqrsDispatcher>();
        services.AddSingleton<IB2BMapper, B2BMapper>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<IEmailNotificationService, InMemoryEmailNotificationService>();

        services.AddCatalogMicroservice($"DataSource=cat_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        services.AddCustomerMicroservice($"DataSource=cust_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        services.AddOrderingMicroservice($"DataSource=ord_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        services.AddInvoicingMicroservice();
        services.AddDeliveryMicroservice();

        return services.BuildServiceProvider();
    }

    // =========================================================================
    // 1. PRODUCT CRUD, SEARCH/FILTER, SOFT-DELETE ARCHIVING & AUTOMAPPER
    // =========================================================================

    [Fact]
    public async Task ProductCrud_SearchFilter_AndSoftDeleteArchive_WorkEndToEndWithEfCore()
    {
        await using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();
        var mapper = provider.GetRequiredService<IMapper>();

        // 1. AutoMapper round-trip check
        var createCmd = new CreateCatalogProductCommand(
            Sku: "  nvme-array-64t  ",
            Name: "  Baie Stockage NVMe-oF 64TB  ",
            Category: "Storage Systems",
            UnitPriceHt: 9000m,
            BulkTierMinQuantity: 3,
            BulkUnitPriceHt: 8100m,
            InitialStock: 8,
            UnitWeightKg: 18.5,
            Description: "All-flash NVMe storage array for AI training datasets.",
            LowStockThreshold: 10);

        var mappedModel = mapper.Map<CreateCatalogProductCommand, CatalogProductDataModel>(createCmd);
        CatalogCrudService.ValidateAndNormalizeProductModel(mappedModel, isCreate: true);
        Assert.Equal("NVME-ARRAY-64T", mappedModel.Sku);
        Assert.Equal("Baie Stockage NVMe-oF 64TB", mappedModel.Name);

        // 2. Create Product via CQRS -> persists in SQLite CatalogProducts + auto-creates Category + VolumeTier
        var created = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(createCmd);
        Assert.Equal("NVME-ARRAY-64T", created.Sku);
        Assert.Equal("Storage Systems", created.Category);
        Assert.NotNull(created.CategoryId);
        Assert.True(created.IsLowStock);
        Assert.False(created.IsOutOfStock);
        Assert.False(created.IsArchived);
        Assert.True(created.CanBeOrdered);
        Assert.Equal(10.0m, created.BulkDiscountPercent);

        // Duplicate SKU must fail with DUPLICATE_SKU
        var dupEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(createCmd));
        Assert.Equal("DUPLICATE_SKU", dupEx.ErrorCode);

        // 3. Search & filter products
        var searchResults = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(
            new GetCatalogProductsQuery(Search: "NVMe-oF", LowStockOnly: true));
        Assert.Single(searchResults);
        Assert.Equal(created.Id, searchResults[0].Id);

        // 4. Update product
        var updated = await sender.SendCommandAsync<UpdateCatalogProductCommand, CatalogProductDto>(
            new UpdateCatalogProductCommand(
                ProductId: created.Id,
                Name: "Baie Stockage NVMe-oF 64TB Gen2",
                Category: "Storage Systems",
                UnitPriceHt: 9500m,
                BulkTierMinQuantity: 3,
                BulkUnitPriceHt: 8550m,
                UnitWeightKg: 19.0,
                Description: "Gen2 controller with dual 200GbE uplinks",
                LowStockThreshold: 10));
        Assert.Equal("Baie Stockage NVMe-oF 64TB Gen2", updated.Name);
        Assert.Equal(9500m, updated.UnitPriceHt);

        // 5. Soft-delete (Archive) -> hidden from default listing, blocks ordering, still visible with includeArchived=true
        var archived = await sender.SendCommandAsync<ArchiveCatalogProductCommand, CatalogProductDto>(
            new ArchiveCatalogProductCommand(created.Id));
        Assert.True(archived.IsArchived);
        Assert.NotNull(archived.ArchivedAtUtc);
        Assert.False(archived.CanBeOrdered);

        var defaultList = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(
            new GetCatalogProductsQuery(Search: "NVME-ARRAY-64T"));
        Assert.Empty(defaultList);

        var withArchivedList = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(
            new GetCatalogProductsQuery(Search: "NVME-ARRAY-64T", IncludeArchived: true));
        Assert.Single(withArchivedList);

        // Unarchive restores availability
        var restored = await sender.SendCommandAsync<UnarchiveCatalogProductCommand, CatalogProductDto>(
            new UnarchiveCatalogProductCommand(created.Id));
        Assert.False(restored.IsArchived);
        Assert.Null(restored.ArchivedAtUtc);
        Assert.True(restored.CanBeOrdered);
    }

    // =========================================================================
    // 2. RELATIONAL CATEGORIES & 3-TABLE INDEPENDENT PRODUCT TAGS
    // =========================================================================

    [Fact]
    public async Task RelationalCategories_And3TableProductTags_WorkIndependentlyAndEnforceIntegrity()
    {
        await using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        // 1. Create a Category and verify slug + product count
        var cat = await sender.SendCommandAsync<CreateCatalogCategoryCommand, CatalogCategoryDto>(
            new CreateCatalogCategoryCommand("Edge Cooling & HVAC", "Precision cooling for micro-datacenters"));
        Assert.Equal("edge-cooling-hvac", cat.Slug);
        Assert.Equal(0, cat.ProductCount);

        // 2. Create 2 independent Product Tags (Table 3: ProductTags)
        var ecoTag = await sender.SendCommandAsync<CreateProductTagCommand, ProductTagDto>(
            new CreateProductTagCommand("Eco-Certified", "ISO-14001", "#10B981"));
        var missionTag = await sender.SendCommandAsync<CreateProductTagCommand, ProductTagDto>(
            new CreateProductTagCommand("Mission-Critical", "99.999% SLA", "#EF4444"));

        // 3. Create Product linked to CategoryId & ecoTag
        var product = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(
            new CreateCatalogProductCommand(
                Sku: "COOL-CDU-200KW",
                Name: "Coolant Distribution Unit 200kW",
                Category: cat.Name,
                UnitPriceHt: 14000m,
                BulkTierMinQuantity: 3,
                BulkUnitPriceHt: 12600m,
                InitialStock: 10,
                UnitWeightKg: 120.0,
                CategoryId: cat.Id,
                TagIds: [ecoTag.Id]));

        Assert.Equal(cat.Id, product.CategoryId);
        Assert.NotNull(product.Tags);
        Assert.Single(product.Tags);

        // Trying to delete a category referenced by a product must fail with CATEGORY_IN_USE
        var inUseEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<DeleteCatalogCategoryCommand, bool>(new DeleteCatalogCategoryCommand(cat.Id)));
        Assert.Equal("CATEGORY_IN_USE", inUseEx.ErrorCode);

        // 4. Assign missionTag via AssignTagToProductCommand & filter by tag
        product = await sender.SendCommandAsync<AssignTagToProductCommand, CatalogProductDto>(
            new AssignTagToProductCommand(product.Id, missionTag.Id));
        Assert.Equal(2, product.Tags!.Count);

        var taggedProducts = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(
            new GetCatalogProductsQuery(Tag: "Mission-Critical"));
        Assert.Single(taggedProducts);
        Assert.Equal(product.Id, taggedProducts[0].Id);

        // 5. Unassign ecoTag -> removes row from ProductTagAssignments without deleting ecoTag from ProductTags
        product = await sender.SendCommandAsync<RemoveTagFromProductCommand, CatalogProductDto>(
            new RemoveTagFromProductCommand(product.Id, ecoTag.Id));
        Assert.Single(product.Tags!);

        var stillExists = await sender.SendQueryAsync<GetProductTagByIdQuery, ProductTagDto?>(
            new GetProductTagByIdQuery(ecoTag.Id));
        Assert.NotNull(stillExists);

        // 6. Verify EF Core tables directly
        using (var db = provider.GetRequiredService<CatalogDbContext>())
        {
            Assert.Equal(4, await db.Categories.CountAsync()); // 3 seeded + 1 new
            Assert.Equal(2, await db.ProductTags.CountAsync());
            Assert.Equal(1, await db.ProductTagAssignments.CountAsync());
        }

        // Delete empty category succeeds
        var emptyCat = await sender.SendCommandAsync<CreateCatalogCategoryCommand, CatalogCategoryDto>(
            new CreateCatalogCategoryCommand("Temporary Category"));
        Assert.True(await sender.SendCommandAsync<DeleteCatalogCategoryCommand, bool>(
            new DeleteCatalogCategoryCommand(emptyCat.Id)));
    }

    // =========================================================================
    // 3. MULTI-TIER B2B VOLUME PRICING
    // =========================================================================

    [Fact]
    public async Task MultiTierVolumePricing_AppliesBestMatchingBracketOnOrdersAndAvailabilityChecks()
    {
        await using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        // Create product with 3 volume tiers: 5+ @ 900, 15+ @ 800, 30+ @ 700 (base = 1000)
        var product = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(
            new CreateCatalogProductCommand(
                Sku: "OPT-QSFP-400G",
                Name: "Transceiver Optique QSFP-DD 400G",
                Category: "Networking",
                UnitPriceHt: 1000m,
                BulkTierMinQuantity: 5,
                BulkUnitPriceHt: 900m,
                InitialStock: 100,
                UnitWeightKg: 0.2,
                VolumePriceTiers:
                [
                    new VolumePriceTierInput(5, 900m),
                    new VolumePriceTierInput(15, 800m),
                    new VolumePriceTierInput(30, 700m)
                ]));

        Assert.Equal(3, product.VolumePriceTiers!.Count);

        // Verify availability & effective pricing for each bracket
        var qty3 = await sender.SendQueryAsync<ValidateProductAvailabilityQuery, ProductAvailabilityCheckDto>(
            new ValidateProductAvailabilityQuery("OPT-QSFP-400G", 3));
        Assert.True(qty3.CanOrder);
        Assert.Equal(1000m, qty3.EffectiveUnitPriceHt);
        Assert.Equal(3000m, qty3.EstimatedLineTotalHt);

        var qty16 = await sender.SendQueryAsync<ValidateProductAvailabilityQuery, ProductAvailabilityCheckDto>(
            new ValidateProductAvailabilityQuery("OPT-QSFP-400G", 16));
        Assert.Equal(800m, qty16.EffectiveUnitPriceHt);

        var qty35 = await sender.SendQueryAsync<ValidateProductAvailabilityQuery, ProductAvailabilityCheckDto>(
            new ValidateProductAvailabilityQuery("OPT-QSFP-400G", 35));
        Assert.Equal(700m, qty35.EffectiveUnitPriceHt);
        Assert.Equal(24500m, qty35.EstimatedLineTotalHt);

        // Order 35 units -> Ordering.Service uses 700 EUR HT tier automatically!
        var order = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "FiberCore Europe SAS",
                BuyerVatNumber: "FR40011223344",
                PaymentTermsDays: 30,
                ShippingDestination: "Paris Data Campus",
                Items: [new OrderLineRequest("OPT-QSFP-400G", 35)],
                AutoConfirm: true));

        Assert.Equal(35 * 700m, order.TotalAmountHt);
    }

    // =========================================================================
    // 4. CLASSIC BACKEND STOCK RULES (ANTI-BYPASS) & SAGA RELEASE ON CANCELLATION
    // =========================================================================

    [Fact]
    public async Task StockBusinessRules_BlockOutOfStock_InsufficientStock_SplitLineBypass_Archived_AndReleaseStockOnCancellation()
    {
        await using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        var limitedProd = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(
            new CreateCatalogProductCommand(
                Sku: "EDGE-BOX-5U",
                Name: "Micro-Edge Appliance 5U",
                Category: "Datacenter Infrastructure",
                UnitPriceHt: 1500m,
                BulkTierMinQuantity: 5,
                BulkUnitPriceHt: 1350m,
                InitialStock: 5,
                UnitWeightKg: 14.0));

        // 1. Cannot order more than available stock (6 > 5) -> INSUFFICIENT_STOCK
        var overStockEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
                new CreatePurchaseOrderCommand(
                    BuyerCompanyName: "Bypass Attempt Co",
                    BuyerVatNumber: "FR8811223344",
                    PaymentTermsDays: 30,
                    ShippingDestination: "Lyon",
                    Items: [new OrderLineRequest("EDGE-BOX-5U", 6)])));
        Assert.Equal("INSUFFICIENT_STOCK", overStockEx.ErrorCode);

        // 2. Anti-bypass: cannot split 6 units into two lines of 3 + 3 to bypass stock check!
        var splitBypassEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
                new CreatePurchaseOrderCommand(
                    BuyerCompanyName: "Smart User Bypass Co",
                    BuyerVatNumber: "FR8811223344",
                    PaymentTermsDays: 30,
                    ShippingDestination: "Lyon",
                    Items:
                    [
                        new OrderLineRequest("EDGE-BOX-5U", 3),
                        new OrderLineRequest("EDGE-BOX-5U", 3)
                    ])));
        Assert.Equal("INSUFFICIENT_STOCK", splitBypassEx.ErrorCode);

        // 3. Valid order of 5 units exhausts stock (Available: 5 -> 0, Reserved: 0 -> 5)
        var validOrder = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Legit Buyer SAS",
                BuyerVatNumber: "FR9911223344",
                PaymentTermsDays: 30,
                ShippingDestination: "Lille",
                Items:
                [
                    new OrderLineRequest("EDGE-BOX-5U", 3),
                    new OrderLineRequest("EDGE-BOX-5U", 2)
                ],
                AutoConfirm: true));

        var afterOrder = await sender.SendQueryAsync<GetCatalogProductByIdQuery, CatalogProductDto?>(
            new GetCatalogProductByIdQuery(limitedProd.Id));
        Assert.NotNull(afterOrder);
        Assert.Equal(0, afterOrder.AvailableStock);
        Assert.Equal(5, afterOrder.ReservedStock);
        Assert.True(afterOrder.IsOutOfStock);
        Assert.False(afterOrder.CanBeOrdered);

        // 4. Cannot add/order an out-of-stock product -> OUT_OF_STOCK
        var oosCheck = await sender.SendQueryAsync<ValidateProductAvailabilityQuery, ProductAvailabilityCheckDto>(
            new ValidateProductAvailabilityQuery("EDGE-BOX-5U", 1));
        Assert.False(oosCheck.CanOrder);
        Assert.Equal("OUT_OF_STOCK", oosCheck.ErrorCode);

        var oosOrderEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
                new CreatePurchaseOrderCommand(
                    BuyerCompanyName: "Late Buyer SAS",
                    BuyerVatNumber: "FR9911223355",
                    PaymentTermsDays: 30,
                    ShippingDestination: "Lille",
                    Items: [new OrderLineRequest("EDGE-BOX-5U", 1)])));
        Assert.Equal("OUT_OF_STOCK", oosOrderEx.ErrorCode);

        // 5. Cancel first order line (3 units) -> Saga automatically releases 3 units back to AvailableStock!
        var firstLineId = validOrder.Lines[0].Id;
        await sender.SendCommandAsync<CancelOrderLineCommand, OrderCancellationResultDto>(
            new CancelOrderLineCommand(validOrder.Id, firstLineId, "Reduced scope"));

        var afterLineCancel = await sender.SendQueryAsync<GetCatalogProductByIdQuery, CatalogProductDto?>(
            new GetCatalogProductByIdQuery(limitedProd.Id));
        Assert.Equal(3, afterLineCancel!.AvailableStock);
        Assert.Equal(2, afterLineCancel.ReservedStock);

        // 6. Cancel remaining order -> Saga releases the remaining 2 units (Available: 3 -> 5, Reserved: 2 -> 0)
        await sender.SendCommandAsync<CancelPurchaseOrderCommand, OrderCancellationResultDto>(
            new CancelPurchaseOrderCommand(validOrder.Id, "Full cancellation"));

        var afterFullCancel = await sender.SendQueryAsync<GetCatalogProductByIdQuery, CatalogProductDto?>(
            new GetCatalogProductByIdQuery(limitedProd.Id));
        Assert.Equal(5, afterFullCancel!.AvailableStock);
        Assert.Equal(0, afterFullCancel.ReservedStock);

        // 7. Archive product -> cannot order archived product (PRODUCT_ARCHIVED)
        await sender.SendCommandAsync<ArchiveCatalogProductCommand, CatalogProductDto>(
            new ArchiveCatalogProductCommand(limitedProd.Id));
        var archEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
                new CreatePurchaseOrderCommand(
                    BuyerCompanyName: "Archive Buyer",
                    BuyerVatNumber: "FR9911223366",
                    PaymentTermsDays: 30,
                    ShippingDestination: "Nantes",
                    Items: [new OrderLineRequest("EDGE-BOX-5U", 1)])));
        Assert.Equal("PRODUCT_ARCHIVED", archEx.ErrorCode);
    }

    // =========================================================================
    // 5. BACK-IN-STOCK REFILL EMAIL ALERTS
    // =========================================================================

    [Fact]
    public async Task StockRefillEmailAlerts_TriggerOnTransitionFromZeroToPositive_AndNeverDoubleNotify()
    {
        await using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();
        var emailService = provider.GetRequiredService<IEmailNotificationService>();

        // Create a product with 2 units initially
        var prod = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(
            new CreateCatalogProductCommand(
                Sku: "PDU-SMART-32A",
                Name: "Smart Metered PDU 32A",
                Category: "Power & Energy",
                UnitPriceHt: 650m,
                BulkTierMinQuantity: 10,
                BulkUnitPriceHt: 580m,
                InitialStock: 2,
                UnitWeightKg: 4.5));

        // 1. Subscribing while IN stock must fail with PRODUCT_IN_STOCK
        var inStockEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<SubscribeStockRefillAlertCommand, StockRefillAlertDto>(
                new SubscribeStockRefillAlertCommand(prod.Id, "buyer@alpha.fr")));
        Assert.Equal("PRODUCT_IN_STOCK", inStockEx.ErrorCode);

        // 2. Deplete stock to 0
        await sender.SendCommandAsync<AdjustCatalogStockCommand, RestockResultDto>(
            new AdjustCatalogStockCommand(prod.Id, 0, "Inventory audit"));

        // Invalid email rejected
        var badEmailEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<SubscribeStockRefillAlertCommand, StockRefillAlertDto>(
                new SubscribeStockRefillAlertCommand(prod.Id, "not-an-email")));
        Assert.Equal("INVALID_EMAIL", badEmailEx.ErrorCode);

        // 3. Subscribe 2 buyers to refill alerts
        var alert1 = await sender.SendCommandAsync<SubscribeStockRefillAlertCommand, StockRefillAlertDto>(
            new SubscribeStockRefillAlertCommand(prod.Id, "Buyer1@Alpha.fr"));
        var alert2 = await sender.SendCommandAsync<SubscribeStockRefillAlertCommand, StockRefillAlertDto>(
            new SubscribeStockRefillAlertCommand(prod.Id, "buyer2@beta.fr"));

        Assert.Equal("buyer1@alpha.fr", alert1.Email);
        Assert.False(alert1.IsNotified);

        // Duplicate subscription rejected
        var dupAlertEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<SubscribeStockRefillAlertCommand, StockRefillAlertDto>(
                new SubscribeStockRefillAlertCommand(prod.Id, "buyer1@alpha.fr")));
        Assert.Equal("DUPLICATE_ALERT_SUBSCRIPTION", dupAlertEx.ErrorCode);

        // 4. Restock from 0 -> 15 units => triggers 2 emails!
        var restockResult = await sender.SendCommandAsync<RestockCatalogProductCommand, RestockResultDto>(
            new RestockCatalogProductCommand(prod.Id, 15));

        Assert.Equal(0, restockResult.PreviousAvailableStock);
        Assert.Equal(15, restockResult.Product.AvailableStock);
        Assert.Equal(2, restockResult.RefillAlertsTriggered);
        Assert.Contains("buyer1@alpha.fr", restockResult.NotifiedEmails);
        Assert.Contains("buyer2@beta.fr", restockResult.NotifiedEmails);

        Assert.Equal(2, emailService.SentEmails.Count);
        Assert.All(emailService.SentEmails, m =>
        {
            Assert.Equal("CatalogStockRefillAlert", m.Category);
            Assert.Contains("PDU-SMART-32A", m.Subject);
        });

        // 5. Subsequent restock (15 -> 25) must NOT trigger any duplicate emails
        var secondRestock = await sender.SendCommandAsync<RestockCatalogProductCommand, RestockResultDto>(
            new RestockCatalogProductCommand(prod.Id, 10));
        Assert.Equal(0, secondRestock.RefillAlertsTriggered);
        Assert.Equal(2, emailService.SentEmails.Count);

        // Pending alerts list is now empty, while full alerts list shows 2 notified alerts
        var pending = await sender.SendQueryAsync<GetProductRefillAlertsQuery, IReadOnlyList<StockRefillAlertDto>>(
            new GetProductRefillAlertsQuery(prod.Id, PendingOnly: true));
        Assert.Empty(pending);

        var allAlerts = await sender.SendQueryAsync<GetProductRefillAlertsQuery, IReadOnlyList<StockRefillAlertDto>>(
            new GetProductRefillAlertsQuery(prod.Id, PendingOnly: false));
        Assert.Equal(2, allAlerts.Count);
        Assert.All(allAlerts, a => Assert.True(a.IsNotified && a.NotifiedAtUtc.HasValue));
        Assert.Equal(alert2.Id, allAlerts[1].Id);

        // 6. Order depletes all 25 units -> buyer subscribes -> order cancelled -> Saga releases stock (0 -> 25) AND triggers refill email!
        var depletingOrder = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Depleter SAS",
                BuyerVatNumber: "FR7711223344",
                PaymentTermsDays: 30,
                ShippingDestination: "Lyon",
                Items: [new OrderLineRequest("PDU-SMART-32A", 25)],
                AutoConfirm: true));

        await sender.SendCommandAsync<SubscribeStockRefillAlertCommand, StockRefillAlertDto>(
            new SubscribeStockRefillAlertCommand(prod.Id, "waiting-on-cancel@gamma.fr"));

        await sender.SendCommandAsync<CancelPurchaseOrderCommand, OrderCancellationResultDto>(
            new CancelPurchaseOrderCommand(depletingOrder.Id, "Cancelled before shipment"));

        Assert.Equal(3, emailService.SentEmails.Count);
        Assert.Contains(emailService.SentEmails, m => m.ToEmail == "waiting-on-cancel@gamma.fr");
    }

    // =========================================================================
    // 6. CUSTOMER FAVORITE PRODUCTS (AUTO + MANUAL) & PERSONAL WISHLIST
    // =========================================================================

    [Fact]
    public async Task CustomerFavorites_AutoAndManual_AndSeparateWishlist_WorkWithLiveCatalogData()
    {
        await using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        var customer = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Atelier Cloud Bretagne SAS",
                SirenSiret: "51234567800019",
                InvoicingAddress: "12 Rue du Port, 35000 Rennes",
                UseInvoicingAddressForDelivery: true,
                ContactEmail: "achats@atelier-cloud.fr"));

        var rack = await sender.SendQueryAsync<GetCatalogProductBySkuQuery, CatalogProductDto?>(
            new GetCatalogProductBySkuQuery("SRV-RACK-42U"));
        var fiberSwitch = await sender.SendQueryAsync<GetCatalogProductBySkuQuery, CatalogProductDto?>(
            new GetCatalogProductBySkuQuery("SW-FIBER-100G"));
        var ups = await sender.SendQueryAsync<GetCatalogProductBySkuQuery, CatalogProductDto?>(
            new GetCatalogProductBySkuQuery("UPS-LFP-10KVA"));

        Assert.NotNull(rack);
        Assert.NotNull(fiberSwitch);
        Assert.NotNull(ups);

        // 1. Customer orders SRV-RACK-42U twice -> auto-detected as frequent favorite (OrderFrequency = 2, TotalUnits = 3)
        await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: customer.Name,
                BuyerVatNumber: customer.SirenSiret,
                PaymentTermsDays: 30,
                ShippingDestination: customer.DeliveryAddress,
                Items: [new OrderLineRequest("SRV-RACK-42U", 2)],
                AutoConfirm: true));

        await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: customer.Name,
                BuyerVatNumber: customer.SirenSiret,
                PaymentTermsDays: 30,
                ShippingDestination: customer.DeliveryAddress,
                Items: [new OrderLineRequest("SRV-RACK-42U", 1)],
                AutoConfirm: true));

        // 2. Customer manually pins SW-FIBER-100G as a favorite product (even though ordered 0 times yet)
        var manualFav = await sender.SendCommandAsync<AddManualFavoriteProductCommand, CustomerFavoriteProductDto>(
            new AddManualFavoriteProductCommand(customer.Id, fiberSwitch.Id));
        Assert.True(manualFav.IsManualFavorite);
        Assert.Equal("SW-FIBER-100G", manualFav.Sku);

        var favorites = await sender.SendQueryAsync<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>(
            new GetCustomerFavoriteProductsQuery(customer.Id));
        Assert.Equal(2, favorites.Count);
        Assert.Equal("SRV-RACK-42U", favorites[0].Sku); // Frequency 2 ranked first
        Assert.Equal(2, favorites[0].OrderFrequency);
        Assert.Equal(3, favorites[0].TotalUnitsOrdered);
        Assert.False(favorites[0].IsManualFavorite);
        Assert.Equal("SW-FIBER-100G", favorites[1].Sku);
        Assert.True(favorites[1].IsManualFavorite);

        // 3. Separate Wishlist (customer's taste, independent of frequent orders!)
        // Make UPS out of stock to prove out-of-stock items CAN still be saved in a Wishlist
        await sender.SendCommandAsync<AdjustCatalogStockCommand, RestockResultDto>(
            new AdjustCatalogStockCommand(ups.Id, 0, "Sold out"));

        var wish1 = await sender.SendCommandAsync<AddProductToWishlistCommand, CustomerWishlistItemDto>(
            new AddProductToWishlistCommand(customer.Id, ups.Id, "For Q3 backup expansion", Priority: 1));
        var wish2 = await sender.SendCommandAsync<AddProductToWishlistCommand, CustomerWishlistItemDto>(
            new AddProductToWishlistCommand(customer.Id, fiberSwitch.Id, "Nice to have for lab", Priority: 4));

        Assert.True(wish1.IsOutOfStock);
        Assert.Equal(1, wish1.Priority);

        var wishlist = await sender.SendQueryAsync<GetCustomerWishlistQuery, IReadOnlyList<CustomerWishlistItemDto>>(
            new GetCustomerWishlistQuery(customer.Id));
        Assert.Equal(2, wishlist.Count);
        Assert.Equal(ups.Id, wishlist[0].ProductId); // Priority 1 comes first
        Assert.Equal(fiberSwitch.Id, wishlist[1].ProductId);

        // Update wishlist priority & note
        var updatedWish = await sender.SendCommandAsync<UpdateWishlistItemCommand, CustomerWishlistItemDto>(
            new UpdateWishlistItemCommand(customer.Id, fiberSwitch.Id, "Urgent for Q2 upgrade", Priority: 1));
        Assert.Equal("Urgent for Q2 upgrade", updatedWish.Note);
        Assert.Equal(1, updatedWish.Priority);

        // Remove manual favorite & wishlist item
        Assert.True(await sender.SendCommandAsync<RemoveManualFavoriteProductCommand, bool>(
            new RemoveManualFavoriteProductCommand(customer.Id, fiberSwitch.Id)));
        Assert.True(await sender.SendCommandAsync<RemoveProductFromWishlistCommand, bool>(
            new RemoveProductFromWishlistCommand(customer.Id, wish2.ProductId)));

        var remainingFavs = await sender.SendQueryAsync<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>(
            new GetCustomerFavoriteProductsQuery(customer.Id));
        Assert.Single(remainingFavs);

        var remainingWish = await sender.SendQueryAsync<GetCustomerWishlistQuery, IReadOnlyList<CustomerWishlistItemDto>>(
            new GetCustomerWishlistQuery(customer.Id));
        Assert.Single(remainingWish);
    }

    // =========================================================================
    // 7. REST API & CATALOG GRAPHQL SUBGRAPH END-TO-END TESTS
    // =========================================================================

    [Fact]
    public async Task CatalogRestAndGraphQL_Endpoints_EnforceStockRules_RefillAlerts_AndWishlist()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        // 1. Create product with 0 initial stock via POST /api/catalog/products
        var createResp = await client.PostAsJsonAsync("/api/catalog/products", new CreateCatalogProductCommand(
            Sku: "AI-DPU-BLUE3",
            Name: "SmartNIC DPU 400GbE",
            Category: "AI Networking",
            UnitPriceHt: 2200m,
            BulkTierMinQuantity: 5,
            BulkUnitPriceHt: 1950m,
            InitialStock: 0,
            UnitWeightKg: 1.2));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var product = await createResp.Content.ReadFromJsonAsync<CatalogProductDto>();
        Assert.NotNull(product);
        Assert.True(product.IsOutOfStock);

        // 2. Frontend/Backend availability endpoint reports OUT_OF_STOCK
        var availResp = await client.GetFromJsonAsync<ProductAvailabilityCheckDto>(
            $"/api/catalog/products/sku/{product.Sku}/availability?quantity=1");
        Assert.NotNull(availResp);
        Assert.False(availResp.CanOrder);
        Assert.Equal("OUT_OF_STOCK", availResp.ErrorCode);

        // 3. Direct POST /api/ordering/orders for out-of-stock item is blocked with 409 Conflict (backend anti-bypass!)
        var blockedOrderResp = await client.PostAsJsonAsync("/api/ordering/orders", new CreatePurchaseOrderCommand(
            BuyerCompanyName: "Bypass Tester",
            BuyerVatNumber: "FR6677889900",
            PaymentTermsDays: 30,
            ShippingDestination: "Paris",
            Items: [new OrderLineRequest(product.Sku, 1)]));
        Assert.Equal(HttpStatusCode.Conflict, blockedOrderResp.StatusCode);
        var blockedJson = await blockedOrderResp.Content.ReadAsStringAsync();
        Assert.Contains("OUT_OF_STOCK", blockedJson);

        // 4. Subscribe to back-in-stock refill alert via REST
        var alertResp = await client.PostAsJsonAsync($"/api/catalog/products/{product.Id}/refill-alerts",
            new SubscribeStockRefillAlertCommand(product.Id, "ops@datacenter.fr"));
        Assert.Equal(HttpStatusCode.Created, alertResp.StatusCode);

        // 5. Restock product via POST /api/catalog/products/{id}/restock -> triggers 1 refill email!
        var restockResp = await client.PostAsJsonAsync($"/api/catalog/products/{product.Id}/restock",
            new RestockCatalogProductCommand(product.Id, 10));
        Assert.Equal(HttpStatusCode.OK, restockResp.StatusCode);
        var restockResult = await restockResp.Content.ReadFromJsonAsync<RestockResultDto>();
        Assert.NotNull(restockResult);
        Assert.Equal(1, restockResult.RefillAlertsTriggered);
        Assert.Contains("ops@datacenter.fr", restockResult.NotifiedEmails);

        // 6. Query Catalog GraphQL subgraph endpoint (/api/catalog/graphql)
        var gqlResp = await client.PostAsJsonAsync("/api/catalog/graphql", new CatalogGraphQLRequest(
            Query: "query($sku: String!, $requestedQuantity: Int!) { __schema productBySku(sku: $sku) { id sku availableStock } validateProductAvailability(sku: $sku, requestedQuantity: $requestedQuantity) { canOrder effectiveUnitPriceHt } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["sku"] = JsonSerializer.SerializeToElement("AI-DPU-BLUE3"),
                ["requestedQuantity"] = JsonSerializer.SerializeToElement(5)
            }));
        Assert.Equal(HttpStatusCode.OK, gqlResp.StatusCode);
        var gqlBody = await gqlResp.Content.ReadFromJsonAsync<CatalogGraphQLResponse>();
        Assert.NotNull(gqlBody);
        Assert.Null(gqlBody.Errors);
        Assert.True(gqlBody.Data!.ContainsKey("productBySku"));
        Assert.True(gqlBody.Data.ContainsKey("validateProductAvailability"));

        // 7. Customer manual favorite + wishlist REST endpoints
        var customers = await client.GetFromJsonAsync<List<B2BCustomerDto>>("/api/customers");
        Assert.NotNull(customers);
        Assert.NotEmpty(customers);
        var customerId = customers[0].Id;

        var addFavResp = await client.PostAsync($"/api/customers/{customerId}/favorite-products/{product.Id}", null);
        Assert.Equal(HttpStatusCode.Created, addFavResp.StatusCode);

        var addWishResp = await client.PostAsJsonAsync($"/api/customers/{customerId}/wishlist",
            new AddProductToWishlistCommand(customerId, product.Id, "For 2027 cluster", 2));
        Assert.Equal(HttpStatusCode.Created, addWishResp.StatusCode);

        var wishlist = await client.GetFromJsonAsync<List<CustomerWishlistItemDto>>($"/api/customers/{customerId}/wishlist");
        Assert.NotNull(wishlist);
        Assert.Single(wishlist);
        Assert.Equal("AI-DPU-BLUE3", wishlist[0].Sku);

        // 8. Soft-delete (Archive) via DELETE /api/catalog/products/{id} and restore via POST .../unarchive
        var archiveResp = await client.DeleteAsync($"/api/catalog/products/{product.Id}");
        Assert.Equal(HttpStatusCode.OK, archiveResp.StatusCode);
        var archivedDto = await archiveResp.Content.ReadFromJsonAsync<CatalogProductDto>();
        Assert.True(archivedDto!.IsArchived);

        var unarchiveResp = await client.PostAsync($"/api/catalog/products/{product.Id}/unarchive", null);
        Assert.Equal(HttpStatusCode.OK, unarchiveResp.StatusCode);
        var restoredDto = await unarchiveResp.Content.ReadFromJsonAsync<CatalogProductDto>();
        Assert.False(restoredDto!.IsArchived);
    }

    [Fact]
    public async Task CustomerGraphQLSubgraph_SupportsFavoriteProductsAndWishlistMutations()
    {
        await using var provider = BuildTestProvider();
        var customerResolver = provider.GetRequiredService<CustomerGraphQLResolver>();
        var sender = provider.GetRequiredService<ISender>();

        var customer = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "GraphQL Wishlist Corp",
                SirenSiret: "61234567800011",
                InvoicingAddress: "1 Rue GraphQL, 75001 Paris",
                UseInvoicingAddressForDelivery: true,
                ContactEmail: "gql@wishlist.fr"));

        var product = await sender.SendQueryAsync<GetCatalogProductBySkuQuery, CatalogProductDto?>(
            new GetCatalogProductBySkuQuery("SRV-RACK-42U"));
        Assert.NotNull(product);

        // Mutation: addToWishlist
        var addWishResp = await customerResolver.ExecuteAsync(new CustomerGraphQLRequest(
            Query: "mutation($customerId: ID!, $productId: ID!, $note: String, $priority: Int) { addToWishlist(customerId: $customerId, productId: $productId, note: $note, priority: $priority) { id sku priority note } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["customerId"] = JsonSerializer.SerializeToElement(customer.Id.ToString()),
                ["productId"] = JsonSerializer.SerializeToElement(product.Id.ToString()),
                ["note"] = JsonSerializer.SerializeToElement("Dream rack"),
                ["priority"] = JsonSerializer.SerializeToElement(1)
            }));
        Assert.Null(addWishResp.Errors);
        var wishDto = Assert.IsType<CustomerWishlistItemDto>(addWishResp.Data!["addToWishlist"]);
        Assert.Equal("SRV-RACK-42U", wishDto.Sku);

        // Mutation: addFavoriteProduct
        var addFavResp = await customerResolver.ExecuteAsync(new CustomerGraphQLRequest(
            Query: "mutation($customerId: ID!, $productId: ID!) { addFavoriteProduct(customerId: $customerId, productId: $productId) { sku isManualFavorite } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["customerId"] = JsonSerializer.SerializeToElement(customer.Id.ToString()),
                ["productId"] = JsonSerializer.SerializeToElement(product.Id.ToString())
            }));
        Assert.Null(addFavResp.Errors);
        var favDto = Assert.IsType<CustomerFavoriteProductDto>(addFavResp.Data!["addFavoriteProduct"]);
        Assert.True(favDto.IsManualFavorite);

        // Query: customerWishlist & customerFavoriteProducts
        var queryResp = await customerResolver.ExecuteAsync(new CustomerGraphQLRequest(
            Query: "query($customerId: ID!) { customerWishlist(customerId: $customerId) { sku } customerFavoriteProducts(customerId: $customerId) { sku isManualFavorite } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["customerId"] = JsonSerializer.SerializeToElement(customer.Id.ToString())
            }));
        Assert.Null(queryResp.Errors);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CustomerWishlistItemDto>>(queryResp.Data!["customerWishlist"]));
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CustomerFavoriteProductDto>>(queryResp.Data["customerFavoriteProducts"]));
    }

    [Fact]
    public async Task EdgeCases_VolumeTierValidation_CategoryUpdate_TagCascadeDelete_AlertUnsubscribe_AndArchivedBlocking()
    {
        await using var provider = BuildTestProvider();
        var sender = provider.GetRequiredService<ISender>();

        // 1. Update Category name & verify slug updates cleanly
        var cat = await sender.SendCommandAsync<CreateCatalogCategoryCommand, CatalogCategoryDto>(
            new CreateCatalogCategoryCommand("Optical Fiber", "Initial description"));
        var updatedCat = await sender.SendCommandAsync<UpdateCatalogCategoryCommand, CatalogCategoryDto>(
            new UpdateCatalogCategoryCommand(cat.Id, "Optical Networking", "Updated description"));
        Assert.Equal("optical-networking", updatedCat.Slug);
        Assert.Equal("Updated description", updatedCat.Description);

        // 2. Create product + tag, then delete tag -> cascades removal from product
        var tag = await sender.SendCommandAsync<CreateProductTagCommand, ProductTagDto>(
            new CreateProductTagCommand("Low-Latency", "Sub-microsecond", "#6366F1"));
        var prod = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(
            new CreateCatalogProductCommand(
                Sku: "OPT-PATCH-400G",
                Name: "Patch Cord OM5 400G",
                Category: updatedCat.Name,
                UnitPriceHt: 200m,
                BulkTierMinQuantity: 10,
                BulkUnitPriceHt: 160m,
                InitialStock: 0,
                UnitWeightKg: 0.3,
                CategoryId: updatedCat.Id,
                TagIds: [tag.Id]));

        Assert.True(await sender.SendCommandAsync<DeleteProductTagCommand, bool>(new DeleteProductTagCommand(tag.Id)));
        var prodAfterTagDel = await sender.SendQueryAsync<GetCatalogProductByIdQuery, CatalogProductDto?>(
            new GetCatalogProductByIdQuery(prod.Id));
        Assert.Empty(prodAfterTagDel!.Tags!);

        // 3. SetProductVolumePriceTiersCommand -> replaces tiers and rejects invalid/duplicate tiers
        var updatedTiers = await sender.SendCommandAsync<SetProductVolumePriceTiersCommand, CatalogProductDto>(
            new SetProductVolumePriceTiersCommand(prod.Id, [new VolumePriceTierInput(5, 180m), new VolumePriceTierInput(20, 140m)]));
        Assert.Equal(2, updatedTiers.VolumePriceTiers!.Count);
        Assert.Equal(5, updatedTiers.BulkTierMinQuantity);
        Assert.Equal(180m, updatedTiers.BulkUnitPriceHt);

        var dupTierEx = await Assert.ThrowsAsync<CatalogDomainException>(() =>
            sender.SendCommandAsync<SetProductVolumePriceTiersCommand, CatalogProductDto>(
                new SetProductVolumePriceTiersCommand(prod.Id, [new VolumePriceTierInput(5, 180m), new VolumePriceTierInput(5, 170m)])));
        Assert.Equal("DUPLICATE_VOLUME_TIER", dupTierEx.ErrorCode);

        // 4. Subscribe & Unsubscribe Stock Refill Alert
        var alert = await sender.SendCommandAsync<SubscribeStockRefillAlertCommand, StockRefillAlertDto>(
            new SubscribeStockRefillAlertCommand(prod.Id, "unsub@client.fr"));
        Assert.True(await sender.SendCommandAsync<UnsubscribeStockRefillAlertCommand, bool>(
            new UnsubscribeStockRefillAlertCommand(alert.Id)));
        var remainingAlerts = await sender.SendQueryAsync<GetProductRefillAlertsQuery, IReadOnlyList<StockRefillAlertDto>>(
            new GetProductRefillAlertsQuery(prod.Id, PendingOnly: false));
        Assert.Empty(remainingAlerts);

        // 5. Archiving a product blocks adding it to Customer Favorites or Wishlist
        await sender.SendCommandAsync<ArchiveCatalogProductCommand, CatalogProductDto>(
            new ArchiveCatalogProductCommand(prod.Id));

        var customer = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Archive Guard Corp",
                SirenSiret: "71234567800012",
                InvoicingAddress: "10 Rue Guard, 69000 Lyon",
                UseInvoicingAddressForDelivery: true,
                ContactEmail: "guard@corp.fr"));

        var favArchEx = await Assert.ThrowsAsync<CustomerDomainException>(() =>
            sender.SendCommandAsync<AddManualFavoriteProductCommand, CustomerFavoriteProductDto>(
                new AddManualFavoriteProductCommand(customer.Id, prod.Id)));
        Assert.Equal("PRODUCT_ARCHIVED", favArchEx.ErrorCode);

        var wishArchEx = await Assert.ThrowsAsync<CustomerDomainException>(() =>
            sender.SendCommandAsync<AddProductToWishlistCommand, CustomerWishlistItemDto>(
                new AddProductToWishlistCommand(customer.Id, prod.Id, "Should fail")));
        Assert.Equal("PRODUCT_ARCHIVED", wishArchEx.ErrorCode);
    }
}
