namespace B2B.GraphQL.Gateway.DevEnvironment;

using B2B.BuildingBlocks.Cqrs;
using Catalog.Service;
using Customer.Service;
using Delivery.Service;
using Invoicing.Service;
using Ordering.Service;

public sealed record DevSeedSummary(
    bool MockDataSeeded,
    int ProductsCount,
    int CustomersCount,
    int OrdersCount,
    int InvoicesCount,
    int ShipmentsCount);

public static class DevMockDataSeeder
{
    public static async Task<DevSeedSummary> SeedDevMockDataAsync(this WebApplication app)
    {
        var forceSeed = string.Equals(
            Environment.GetEnvironmentVariable("B2B_SEED_MOCK_DATA"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var seedFullMockup = app.Environment.IsDevelopment() || forceSeed;

        using var scope = app.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await SeedBaselineDataAsync(sender);

        if (seedFullMockup)
        {
            await SeedExtendedDevMockupDataAsync(sender);
        }

        var products = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(new GetCatalogProductsQuery());
        var customers = await sender.SendQueryAsync<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>(new GetB2BCustomersQuery());
        var orders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(new GetPurchaseOrdersQuery());
        var invoices = await sender.SendQueryAsync<GetInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>(new GetInvoicesQuery());
        var shipments = await sender.SendQueryAsync<GetShipmentsQuery, IReadOnlyList<B2BShipmentDto>>(new GetShipmentsQuery());

        return new DevSeedSummary(
            MockDataSeeded: seedFullMockup,
            ProductsCount: products.Count,
            CustomersCount: customers.Count,
            OrdersCount: orders.Count,
            InvoicesCount: invoices.Count,
            ShipmentsCount: shipments.Count);
    }

    private static async Task SeedBaselineDataAsync(ISender sender)
    {
        var existingCustomers = await sender.SendQueryAsync<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>(new GetB2BCustomersQuery());
        if (existingCustomers.Count == 0)
        {
            await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
                new CreateB2BCustomerCommand(
                    Name: "Alpine Cloud Datacenters SAS",
                    SirenSiret: "42889123456001",
                    InvoicingAddress: "Parc Technologique de Grenoble, 38000 France",
                    DeliveryAddress: null,
                    UseInvoicingAddressForDelivery: true,
                    ContactEmail: "procurement@alpine-cloud.fr",
                    PhoneNumber: "+33 4 76 00 11 22",
                    Tags:
                    [
                        new CustomTagInput("VIP", "Strategic Account", "#7C3AED"),
                        new CustomTagInput("Datacenter", "Tier-IV", "#10B981")
                    ]));
        }

        var existingOrders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(new GetPurchaseOrdersQuery());
        if (existingOrders.Count == 0)
        {
            await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
                new CreatePurchaseOrderCommand(
                    BuyerCompanyName: "Alpine Cloud Datacenters SAS",
                    BuyerVatNumber: "FR42889123456",
                    PaymentTermsDays: 30,
                    ShippingDestination: "Parc Technologique de Grenoble, 38000 France",
                    Items:
                    [
                        new OrderLineRequest("SRV-RACK-42U", 6),
                        new OrderLineRequest("SW-FIBER-100G", 4)
                    ],
                    AutoConfirm: true));
        }
    }

    private static async Task SeedExtendedDevMockupDataAsync(ISender sender)
    {
        // 1. Seed additional B2B Catalog mockup products (idempotent by SKU)
        var mockupProducts = new[]
        {
            new CreateCatalogProductCommand(
                Sku: "NVME-ARRAY-24TB",
                Name: "Baie Stockage All-Flash NVMe 24To Redondante",
                Category: "Storage & NVMe",
                UnitPriceHt: 3600m,
                BulkTierMinQuantity: 3,
                BulkUnitPriceHt: 3150m,
                InitialStock: 24,
                UnitWeightKg: 18.5),
            new CreateCatalogProductCommand(
                Sku: "COOL-CDU-150KW",
                Name: "Unité Distribution Refroidissement Liquide CDU 150kW",
                Category: "Cooling & Thermal",
                UnitPriceHt: 8900m,
                BulkTierMinQuantity: 2,
                BulkUnitPriceHt: 7900m,
                InitialStock: 9,
                UnitWeightKg: 112.0),
            new CreateCatalogProductCommand(
                Sku: "FW-EDGE-40G",
                Name: "Pare-Feu Industriel Edge 40GbE Haute Disponibilité",
                Category: "Edge Security",
                UnitPriceHt: 2150m,
                BulkTierMinQuantity: 5,
                BulkUnitPriceHt: 1890m,
                InitialStock: 30,
                UnitWeightKg: 7.2)
        };

        foreach (var productCmd in mockupProducts)
        {
            var existing = await sender.SendQueryAsync<GetCatalogProductBySkuQuery, CatalogProductDto?>(
                new GetCatalogProductBySkuQuery(productCmd.Sku));
            if (existing is null)
            {
                await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(productCmd);
            }
        }

        // 2. Seed additional B2B Customers (idempotent by SIREN/SIRET)
        var customers = await sender.SendQueryAsync<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>(new GetB2BCustomersQuery());
        var existingSirens = customers.Select(c => c.SirenSiret).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mockupCustomers = new[]
        {
            new CreateB2BCustomerCommand(
                Name: "Helios Industrial Logistics SA",
                SirenSiret: "51234098700024",
                InvoicingAddress: "18 Rue du Fret International, 69125 Colombier-Saugnieu, France",
                DeliveryAddress: "Hub Logistique Terminal B, 69125 Colombier-Saugnieu, France",
                UseInvoicingAddressForDelivery: false,
                ContactEmail: "supply@helios-logistics.fr",
                PhoneNumber: "+33 4 72 99 88 10",
                Tags:
                [
                    new CustomTagInput("Enterprise", "Multi-Site Contract", "#2563EB"),
                    new CustomTagInput("Freight", "Heavy Pallet", "#F59E0B")
                ]),
            new CreateB2BCustomerCommand(
                Name: "Nordic Photonics Systems AB",
                SirenSiret: "63490123400039",
                InvoicingAddress: "Kistagangen 16, 164 40 Kista, Sweden",
                DeliveryAddress: null,
                UseInvoicingAddressForDelivery: true,
                ContactEmail: "orders@nordic-photonics.se",
                PhoneNumber: "+46 8 501 22 400",
                Tags:
                [
                    new CustomTagInput("EU-Export", "Intra-EU VAT", "#06B6D4"),
                    new CustomTagInput("OEM", "Hardware Partner", "#8B5CF6")
                ])
        };

        foreach (var customerCmd in mockupCustomers.Where(c => !existingSirens.Contains(c.SirenSiret)))
        {
            await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(customerCmd);
        }

        // 3. Seed additional Purchase Orders (1 Confirmed triggering Invoice+Shipment + 1 Draft)
        var orders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(new GetPurchaseOrdersQuery());
        var existingVats = orders.Select(o => o.BuyerVatNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingVats.Contains("FR51234098700"))
        {
            await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
                new CreatePurchaseOrderCommand(
                    BuyerCompanyName: "Helios Industrial Logistics SA",
                    BuyerVatNumber: "FR51234098700",
                    PaymentTermsDays: 45,
                    ShippingDestination: "Hub Logistique Terminal B, 69125 Colombier-Saugnieu, France",
                    Items:
                    [
                        new OrderLineRequest("NVME-ARRAY-24TB", 3),
                        new OrderLineRequest("FW-EDGE-40G", 5)
                    ],
                    AutoConfirm: true,
                    ContactEmail: "supply@helios-logistics.fr"));
        }

        if (!existingVats.Contains("SE63490123401"))
        {
            await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
                new CreatePurchaseOrderCommand(
                    BuyerCompanyName: "Nordic Photonics Systems AB",
                    BuyerVatNumber: "SE63490123401",
                    PaymentTermsDays: 30,
                    ShippingDestination: "Kistagangen 16, 164 40 Kista, Sweden",
                    Items:
                    [
                        new OrderLineRequest("COOL-CDU-150KW", 2)
                    ],
                    AutoConfirm: false,
                    ContactEmail: "orders@nordic-photonics.se"));
        }
    }
}
