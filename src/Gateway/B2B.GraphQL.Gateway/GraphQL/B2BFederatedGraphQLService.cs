namespace B2B.GraphQL.Gateway.GraphQL;

using System.Text.Json;
using B2B.BuildingBlocks.Cqrs;
using Catalog.Service;
using Delivery.Service;
using Invoicing.Service;
using Ordering.Service;

public sealed record GraphQLRequest(
    string Query,
    string? OperationName = null,
    Dictionary<string, JsonElement>? Variables = null);

public sealed record GraphQLError(string Message);

public sealed record GraphQLResponse(
    Dictionary<string, object?>? Data,
    IReadOnlyList<GraphQLError>? Errors = null);

/// <summary>
/// Stitched GraphQL node combining data from Ordering.Service, Invoicing.Service, Delivery.Service, and Catalog.Service.
/// </summary>
public sealed record FederatedOrderNode(
    Guid Id,
    string OrderNumber,
    string BuyerCompanyName,
    string BuyerVatNumber,
    int PaymentTermsDays,
    string ShippingDestination,
    string Status,
    decimal TotalAmountHt,
    int TotalUnits,
    IReadOnlyList<PurchaseOrderLineDto> Lines,
    B2BInvoiceDto? Invoice,
    B2BShipmentDto? Shipment);

public sealed record B2BDashboardMetrics(
    int TotalProducts,
    int LowStockProducts,
    int TotalOrders,
    decimal TotalOrderVolumeHt,
    int TotalInvoices,
    decimal TotalInvoicedTtc,
    int ActiveShipments);

public sealed class B2BFederatedGraphQLService(ISender sender)
{
    public const string SdlSchema = """
        schema {
          query: Query
          mutation: Mutation
        }

        type Query {
          dashboard: B2BDashboardMetrics!
          catalog(category: String): [CatalogProduct!]!
          productBySku(sku: String!): CatalogProduct
          orders: [FederatedOrderNode!]!
          orderById(id: ID!): FederatedOrderNode
          invoices: [B2BInvoice!]!
          shipments: [B2BShipment!]!
        }

        type Mutation {
          createCatalogProduct(sku: String!, name: String!, category: String!, unitPriceHt: Float!, bulkTierMinQuantity: Int!, bulkUnitPriceHt: Float!, initialStock: Int!, unitWeightKg: Float!): CatalogProduct!
          createPurchaseOrder(buyerCompanyName: String!, buyerVatNumber: String!, paymentTermsDays: Int!, shippingDestination: String!): FederatedOrderNode!
          markInvoicePaid(invoiceId: ID!): B2BInvoice
          advanceShipmentStatus(shipmentId: ID!): B2BShipment
        }

        type FederatedOrderNode {
          id: ID!
          orderNumber: String!
          buyerCompanyName: String!
          buyerVatNumber: String!
          paymentTermsDays: Int!
          shippingDestination: String!
          status: String!
          totalAmountHt: Float!
          totalUnits: Int!
          lines: [PurchaseOrderLine!]!
          invoice: B2BInvoice
          shipment: B2BShipment
        }
        """;

    public async Task<GraphQLResponse> ExecuteAsync(GraphQLRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            return new GraphQLResponse(null, [new GraphQLError("GraphQL query string cannot be empty.")]);
        }

        var q = request.Query.Trim();
        var vars = request.Variables ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (q.StartsWith("mutation", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteMutationAsync(q, vars, ct);
            }

            return await ExecuteQueryAsync(q, vars, ct);
        }
        catch (Exception ex)
        {
            return new GraphQLResponse(null, [new GraphQLError(ex.Message)]);
        }
    }

    private async Task<GraphQLResponse> ExecuteQueryAsync(
        string queryText,
        Dictionary<string, JsonElement> vars,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        if (ContainsRootField(queryText, "__schema"))
        {
            data["__schema"] = new
            {
                queryType = new { name = "Query" },
                mutationType = new { name = "Mutation" },
                sdl = SdlSchema
            };
        }

        if (ContainsRootField(queryText, "dashboard"))
        {
            var products = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(new GetCatalogProductsQuery(), ct);
            var orders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(new GetPurchaseOrdersQuery(), ct);
            var invoices = await sender.SendQueryAsync<GetInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>(new GetInvoicesQuery(), ct);
            var shipments = await sender.SendQueryAsync<GetShipmentsQuery, IReadOnlyList<B2BShipmentDto>>(new GetShipmentsQuery(), ct);

            data["dashboard"] = new B2BDashboardMetrics(
                TotalProducts: products.Count,
                LowStockProducts: products.Count(p => p.IsLowStock),
                TotalOrders: orders.Count,
                TotalOrderVolumeHt: orders.Sum(o => o.TotalAmountHt),
                TotalInvoices: invoices.Count,
                TotalInvoicedTtc: invoices.Sum(i => i.TotalTtc),
                ActiveShipments: shipments.Count(s => s.Status != "Delivered"));
        }

        if (ContainsRootField(queryText, "catalog"))
        {
            var category = TryGetStringVar(vars, "category");
            data["catalog"] = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(
                new GetCatalogProductsQuery(category), ct);
        }

        if (ContainsRootField(queryText, "productBySku"))
        {
            var sku = TryGetStringVar(vars, "sku") ?? "SRV-RACK-42U";
            data["productBySku"] = await sender.SendQueryAsync<GetCatalogProductBySkuQuery, CatalogProductDto?>(
                new GetCatalogProductBySkuQuery(sku), ct);
        }

        if (ContainsRootField(queryText, "orders"))
        {
            var orders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(new GetPurchaseOrdersQuery(), ct);
            var federated = new List<FederatedOrderNode>(orders.Count);

            foreach (var order in orders)
            {
                federated.Add(await StitchOrderNodeAsync(order, ct));
            }

            data["orders"] = federated;
        }

        if (ContainsRootField(queryText, "orderById"))
        {
            var idStr = TryGetStringVar(vars, "id");
            if (Guid.TryParse(idStr, out var orderId))
            {
                var order = await sender.SendQueryAsync<GetPurchaseOrderByIdQuery, PurchaseOrderDto?>(new GetPurchaseOrderByIdQuery(orderId), ct);
                data["orderById"] = order is null ? null : await StitchOrderNodeAsync(order, ct);
            }
            else
            {
                data["orderById"] = null;
            }
        }

        if (ContainsRootField(queryText, "invoices"))
        {
            data["invoices"] = await sender.SendQueryAsync<GetInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>(new GetInvoicesQuery(), ct);
        }

        if (ContainsRootField(queryText, "shipments"))
        {
            data["shipments"] = await sender.SendQueryAsync<GetShipmentsQuery, IReadOnlyList<B2BShipmentDto>>(new GetShipmentsQuery(), ct);
        }

        if (data.Count == 0)
        {
            return new GraphQLResponse(null, [new GraphQLError("Unknown GraphQL query root field.")]);
        }

        return new GraphQLResponse(data);
    }

    private async Task<GraphQLResponse> ExecuteMutationAsync(
        string mutationText,
        Dictionary<string, JsonElement> vars,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        if (ContainsRootField(mutationText, "createCatalogProduct"))
        {
            var cmd = new CreateCatalogProductCommand(
                Sku: TryGetStringVar(vars, "sku") ?? "NEW-SKU-01",
                Name: TryGetStringVar(vars, "name") ?? "New B2B Equipment",
                Category: TryGetStringVar(vars, "category") ?? "Hardware",
                UnitPriceHt: TryGetDecimalVar(vars, "unitPriceHt", 1000m),
                BulkTierMinQuantity: TryGetIntVar(vars, "bulkTierMinQuantity", 5),
                BulkUnitPriceHt: TryGetDecimalVar(vars, "bulkUnitPriceHt", 850m),
                InitialStock: TryGetIntVar(vars, "initialStock", 25),
                UnitWeightKg: TryGetDoubleVar(vars, "unitWeightKg", 10.0));

            data["createCatalogProduct"] = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(cmd, ct);
        }

        if (ContainsRootField(mutationText, "createPurchaseOrder"))
        {
            var items = ParseOrderLineRequests(vars);
            var cmd = new CreatePurchaseOrderCommand(
                BuyerCompanyName: TryGetStringVar(vars, "buyerCompanyName") ?? "Enterprise Corp SAS",
                BuyerVatNumber: TryGetStringVar(vars, "buyerVatNumber") ?? "FR9988776655",
                PaymentTermsDays: TryGetIntVar(vars, "paymentTermsDays", 30),
                ShippingDestination: TryGetStringVar(vars, "shippingDestination") ?? "Lyon Hub Logistique, France",
                Items: items,
                AutoConfirm: true);

            var createdOrder = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(cmd, ct);
            data["createPurchaseOrder"] = await StitchOrderNodeAsync(createdOrder, ct);
        }

        if (ContainsRootField(mutationText, "markInvoicePaid"))
        {
            var invoiceIdStr = TryGetStringVar(vars, "invoiceId");
            if (Guid.TryParse(invoiceIdStr, out var invoiceId))
            {
                data["markInvoicePaid"] = await sender.SendCommandAsync<MarkInvoicePaidCommand, B2BInvoiceDto?>(
                    new MarkInvoicePaidCommand(invoiceId), ct);
            }
        }

        if (ContainsRootField(mutationText, "advanceShipmentStatus"))
        {
            var shipmentIdStr = TryGetStringVar(vars, "shipmentId");
            if (Guid.TryParse(shipmentIdStr, out var shipmentId))
            {
                data["advanceShipmentStatus"] = await sender.SendCommandAsync<AdvanceShipmentStatusCommand, B2BShipmentDto?>(
                    new AdvanceShipmentStatusCommand(shipmentId), ct);
            }
        }

        if (data.Count == 0)
        {
            return new GraphQLResponse(null, [new GraphQLError("Unknown GraphQL mutation field.")]);
        }

        return new GraphQLResponse(data);
    }

    private async Task<FederatedOrderNode> StitchOrderNodeAsync(PurchaseOrderDto order, CancellationToken ct)
    {
        var invoice = await sender.SendQueryAsync<GetInvoiceByOrderIdQuery, B2BInvoiceDto?>(
            new GetInvoiceByOrderIdQuery(order.Id), ct);
        var shipment = await sender.SendQueryAsync<GetShipmentByOrderIdQuery, B2BShipmentDto?>(
            new GetShipmentByOrderIdQuery(order.Id), ct);

        return new FederatedOrderNode(
            order.Id,
            order.OrderNumber,
            order.BuyerCompanyName,
            order.BuyerVatNumber,
            order.PaymentTermsDays,
            order.ShippingDestination,
            order.Status,
            order.TotalAmountHt,
            order.TotalUnits,
            order.Lines,
            invoice,
            shipment);
    }

    private static bool ContainsRootField(string query, string fieldName) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            query,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(fieldName)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

    private static string? TryGetStringVar(Dictionary<string, JsonElement> vars, string key) =>
        vars.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int TryGetIntVar(Dictionary<string, JsonElement> vars, string key, int fallback) =>
        vars.TryGetValue(key, out var el) && el.TryGetInt32(out var val) ? val : fallback;

    private static decimal TryGetDecimalVar(Dictionary<string, JsonElement> vars, string key, decimal fallback) =>
        vars.TryGetValue(key, out var el) && el.TryGetDecimal(out var val) ? val : fallback;

    private static double TryGetDoubleVar(Dictionary<string, JsonElement> vars, string key, double fallback) =>
        vars.TryGetValue(key, out var el) && el.TryGetDouble(out var val) ? val : fallback;

    private static IReadOnlyList<OrderLineRequest> ParseOrderLineRequests(Dictionary<string, JsonElement> vars)
    {
        if (vars.TryGetValue("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<OrderLineRequest>();
            foreach (var item in itemsEl.EnumerateArray())
            {
                var sku = item.TryGetProperty("sku", out var s) ? s.GetString() ?? "SRV-RACK-42U" : "SRV-RACK-42U";
                var qty = item.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qv) ? qv : 1;
                list.Add(new OrderLineRequest(sku, qty));
            }
            if (list.Count > 0) return list;
        }

        return [new OrderLineRequest("SRV-RACK-42U", 5)];
    }
}
