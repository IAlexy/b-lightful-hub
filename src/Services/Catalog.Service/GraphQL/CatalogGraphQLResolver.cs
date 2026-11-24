namespace Catalog.Service.GraphQL;

using System.Text.Json;
using B2B.BuildingBlocks.Cqrs;
using Catalog.Service.DataModels;

public sealed record CatalogGraphQLRequest(
    string Query,
    string? OperationName = null,
    Dictionary<string, JsonElement>? Variables = null);

public sealed record CatalogGraphQLError(string Message, string? ErrorCode = null);

public sealed record CatalogGraphQLResponse(
    Dictionary<string, object?>? Data,
    IReadOnlyList<CatalogGraphQLError>? Errors = null);

/// <summary>
/// Catalog Microservice GraphQL Subgraph Resolver supporting:
/// - Queries: catalog/products, productById, productBySku, validateProductAvailability,
///            categories, categoryById, productTags, productTagById, productRefillAlerts
/// - Mutations: createCatalogProduct, updateCatalogProduct, archiveCatalogProduct, unarchiveCatalogProduct,
///              createCatalogCategory, updateCatalogCategory, deleteCatalogCategory,
///              createProductTag, deleteProductTag, assignTagToProduct, removeTagFromProduct,
///              setProductVolumePriceTiers, restockCatalogProduct, adjustCatalogStock,
///              subscribeStockRefillAlert, unsubscribeStockRefillAlert
/// </summary>
public sealed class CatalogGraphQLResolver(ISender sender)
{
    public const string SubgraphSdl = """
        type CatalogCategory {
          id: ID!
          name: String!
          slug: String!
          description: String
          productCount: Int!
          createdAtUtc: String!
        }

        type ProductTag {
          id: ID!
          name: String!
          value: String
          color: String!
        }

        input ProductTagInput {
          name: String!
          value: String
          color: String = "#0EA5E9"
        }

        type VolumePriceTier {
          id: ID!
          minQuantity: Int!
          unitPriceHt: Float!
          discountPercent: Float!
        }

        input VolumePriceTierInput {
          minQuantity: Int!
          unitPriceHt: Float!
        }

        type CatalogProduct {
          id: ID!
          sku: String!
          name: String!
          description: String
          categoryId: ID
          category: String!
          unitPriceHt: Float!
          bulkTierMinQuantity: Int!
          bulkUnitPriceHt: Float!
          bulkDiscountPercent: Float!
          availableStock: Int!
          reservedStock: Int!
          lowStockThreshold: Int!
          unitWeightKg: Float!
          isLowStock: Boolean!
          isOutOfStock: Boolean!
          isArchived: Boolean!
          canBeOrdered: Boolean!
          volumePriceTiers: [VolumePriceTier!]!
          tags: [ProductTag!]!
        }

        type ProductAvailabilityCheck {
          sku: String!
          productId: ID
          exists: Boolean!
          isArchived: Boolean!
          isOutOfStock: Boolean!
          availableStock: Int!
          requestedQuantity: Int!
          canOrder: Boolean!
          effectiveUnitPriceHt: Float
          estimatedLineTotalHt: Float
          errorCode: String
          message: String
        }

        type StockRefillAlert {
          id: ID!
          productId: ID!
          sku: String!
          productName: String!
          customerId: ID
          email: String!
          isNotified: Boolean!
          createdAtUtc: String!
          notifiedAtUtc: String
        }

        type RestockResult {
          product: CatalogProduct!
          previousAvailableStock: Int!
          deltaQuantity: Int!
          refillAlertsTriggered: Int!
          notifiedEmails: [String!]!
        }

        type Query {
          catalog(category: String, search: String, tag: String, lowStockOnly: Boolean, inStockOnly: Boolean, includeArchived: Boolean = false): [CatalogProduct!]!
          products(category: String, search: String, tag: String, lowStockOnly: Boolean, inStockOnly: Boolean, includeArchived: Boolean = false): [CatalogProduct!]!
          productById(id: ID!, includeArchived: Boolean = true): CatalogProduct
          productBySku(sku: String!, includeArchived: Boolean = false): CatalogProduct
          validateProductAvailability(sku: String!, requestedQuantity: Int!): ProductAvailabilityCheck!
          categories(search: String): [CatalogCategory!]!
          categoryById(id: ID!): CatalogCategory
          productTags(search: String): [ProductTag!]!
          productTagById(id: ID!): ProductTag
          productRefillAlerts(productId: ID!, pendingOnly: Boolean = true): [StockRefillAlert!]!
        }

        type Mutation {
          createCatalogProduct(sku: String!, name: String!, category: String!, unitPriceHt: Float!, bulkTierMinQuantity: Int!, bulkUnitPriceHt: Float!, initialStock: Int!, unitWeightKg: Float!, description: String, categoryId: ID, lowStockThreshold: Int = 15, volumePriceTiers: [VolumePriceTierInput!], tagIds: [ID!], tags: [ProductTagInput!]): CatalogProduct!
          updateCatalogProduct(productId: ID!, name: String!, category: String!, unitPriceHt: Float!, bulkTierMinQuantity: Int!, bulkUnitPriceHt: Float!, unitWeightKg: Float!, description: String, categoryId: ID, lowStockThreshold: Int = 15, volumePriceTiers: [VolumePriceTierInput!], tagIds: [ID!], tags: [ProductTagInput!]): CatalogProduct!
          archiveCatalogProduct(productId: ID!): CatalogProduct!
          unarchiveCatalogProduct(productId: ID!): CatalogProduct!
          createCatalogCategory(name: String!, description: String): CatalogCategory!
          updateCatalogCategory(categoryId: ID!, name: String!, description: String): CatalogCategory!
          deleteCatalogCategory(categoryId: ID!): Boolean!
          createProductTag(name: String!, value: String, color: String = "#0EA5E9"): ProductTag!
          deleteProductTag(tagId: ID!): Boolean!
          assignTagToProduct(productId: ID!, tagId: ID!): CatalogProduct!
          removeTagFromProduct(productId: ID!, tagId: ID!): CatalogProduct!
          setProductVolumePriceTiers(productId: ID!, tiers: [VolumePriceTierInput!]!): CatalogProduct!
          restockCatalogProduct(productId: ID!, quantityAdded: Int!): RestockResult!
          adjustCatalogStock(productId: ID!, newAvailableStock: Int!, reason: String): RestockResult!
          subscribeStockRefillAlert(productId: ID!, email: String!, customerId: ID): StockRefillAlert!
          unsubscribeStockRefillAlert(alertId: ID!): Boolean!
        }
        """;

    public async Task<CatalogGraphQLResponse> ExecuteAsync(
        CatalogGraphQLRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            return new CatalogGraphQLResponse(null, [new CatalogGraphQLError("GraphQL query cannot be empty.")]);
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
        catch (CatalogDomainException ex)
        {
            return new CatalogGraphQLResponse(null, [new CatalogGraphQLError(ex.Message, ex.ErrorCode)]);
        }
        catch (Exception ex)
        {
            return new CatalogGraphQLResponse(null, [new CatalogGraphQLError(ex.Message)]);
        }
    }

    private async Task<CatalogGraphQLResponse> ExecuteQueryAsync(
        string queryText,
        Dictionary<string, JsonElement> vars,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        if (ContainsField(queryText, "__schema"))
        {
            data["__schema"] = new { sdl = SubgraphSdl };
        }

        await ResolveProductQueriesAsync(queryText, vars, data, ct);
        await ResolveTaxonomyAndAlertQueriesAsync(queryText, vars, data, ct);

        if (data.Count == 0)
        {
            return new CatalogGraphQLResponse(null, [new CatalogGraphQLError("Unknown Catalog GraphQL query field.")]);
        }

        return new CatalogGraphQLResponse(data);
    }

    private async Task ResolveProductQueriesAsync(
        string queryText,
        Dictionary<string, JsonElement> vars,
        Dictionary<string, object?> data,
        CancellationToken ct)
    {
        if (ContainsField(queryText, "catalog") || ContainsField(queryText, "products"))
        {
            var list = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(
                new GetCatalogProductsQuery(
                    Category: TryGetString(vars, "category"),
                    Search: TryGetString(vars, "search"),
                    Tag: TryGetString(vars, "tag"),
                    LowStockOnly: TryGetNullableBool(vars, "lowStockOnly"),
                    InStockOnly: TryGetNullableBool(vars, "inStockOnly"),
                    IncludeArchived: TryGetBool(vars, "includeArchived", false)), ct);

            if (ContainsField(queryText, "catalog"))
            {
                data["catalog"] = list;
            }
            if (ContainsField(queryText, "products"))
            {
                data["products"] = list;
            }
        }

        if (ContainsField(queryText, "productById"))
        {
            var id = ParseGuidVar(vars, "id", "productId");
            var includeArchived = TryGetBool(vars, "includeArchived", true);
            data["productById"] = await sender.SendQueryAsync<GetCatalogProductByIdQuery, CatalogProductDto?>(
                new GetCatalogProductByIdQuery(id, includeArchived), ct);
        }

        if (ContainsField(queryText, "productBySku"))
        {
            var sku = TryGetString(vars, "sku") ?? string.Empty;
            var includeArchived = TryGetBool(vars, "includeArchived", false);
            data["productBySku"] = await sender.SendQueryAsync<GetCatalogProductBySkuQuery, CatalogProductDto?>(
                new GetCatalogProductBySkuQuery(sku, includeArchived), ct);
        }

        if (ContainsField(queryText, "validateProductAvailability"))
        {
            var sku = TryGetString(vars, "sku") ?? string.Empty;
            var qty = TryGetInt(vars, "requestedQuantity", 1);
            data["validateProductAvailability"] = await sender.SendQueryAsync<ValidateProductAvailabilityQuery, ProductAvailabilityCheckDto>(
                new ValidateProductAvailabilityQuery(sku, qty), ct);
        }
    }

    private async Task ResolveTaxonomyAndAlertQueriesAsync(
        string queryText,
        Dictionary<string, JsonElement> vars,
        Dictionary<string, object?> data,
        CancellationToken ct)
    {
        if (ContainsField(queryText, "categories"))
        {
            data["categories"] = await sender.SendQueryAsync<GetCatalogCategoriesQuery, IReadOnlyList<CatalogCategoryDto>>(
                new GetCatalogCategoriesQuery(TryGetString(vars, "search")), ct);
        }

        if (ContainsField(queryText, "categoryById"))
        {
            var id = ParseGuidVar(vars, "id", "categoryId");
            data["categoryById"] = await sender.SendQueryAsync<GetCatalogCategoryByIdQuery, CatalogCategoryDto?>(
                new GetCatalogCategoryByIdQuery(id), ct);
        }

        if (ContainsField(queryText, "productTags"))
        {
            data["productTags"] = await sender.SendQueryAsync<GetProductTagsQuery, IReadOnlyList<ProductTagDto>>(
                new GetProductTagsQuery(TryGetString(vars, "search")), ct);
        }

        if (ContainsField(queryText, "productTagById"))
        {
            var id = ParseGuidVar(vars, "id", "tagId");
            data["productTagById"] = await sender.SendQueryAsync<GetProductTagByIdQuery, ProductTagDto?>(
                new GetProductTagByIdQuery(id), ct);
        }

        if (ContainsField(queryText, "productRefillAlerts"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            var pendingOnly = TryGetBool(vars, "pendingOnly", true);
            data["productRefillAlerts"] = await sender.SendQueryAsync<GetProductRefillAlertsQuery, IReadOnlyList<StockRefillAlertDto>>(
                new GetProductRefillAlertsQuery(productId, pendingOnly), ct);
        }
    }

    private async Task<CatalogGraphQLResponse> ExecuteMutationAsync(
        string mutationText,
        Dictionary<string, JsonElement> vars,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        await ResolveProductMutationsAsync(mutationText, vars, data, ct);
        await ResolveTaxonomyMutationsAsync(mutationText, vars, data, ct);
        await ResolveStockAndAlertMutationsAsync(mutationText, vars, data, ct);

        if (data.Count == 0)
        {
            return new CatalogGraphQLResponse(null, [new CatalogGraphQLError("Unknown Catalog GraphQL mutation field.")]);
        }

        return new CatalogGraphQLResponse(data);
    }

    private async Task ResolveProductMutationsAsync(
        string mutationText,
        Dictionary<string, JsonElement> vars,
        Dictionary<string, object?> data,
        CancellationToken ct)
    {
        if (ContainsField(mutationText, "createCatalogProduct"))
        {
            var cmd = new CreateCatalogProductCommand(
                Sku: TryGetString(vars, "sku") ?? string.Empty,
                Name: TryGetString(vars, "name") ?? string.Empty,
                Category: TryGetString(vars, "category") ?? string.Empty,
                UnitPriceHt: TryGetDecimal(vars, "unitPriceHt", 0m),
                BulkTierMinQuantity: TryGetInt(vars, "bulkTierMinQuantity", 10),
                BulkUnitPriceHt: TryGetDecimal(vars, "bulkUnitPriceHt", 0m),
                InitialStock: TryGetInt(vars, "initialStock", 0),
                UnitWeightKg: TryGetDouble(vars, "unitWeightKg", 1.0),
                Description: TryGetString(vars, "description"),
                CategoryId: TryParseOptionalGuid(vars, "categoryId"),
                LowStockThreshold: TryGetInt(vars, "lowStockThreshold", 15),
                VolumePriceTiers: ParseVolumeTiers(vars, "volumePriceTiers"),
                TagIds: ParseGuidList(vars, "tagIds"),
                Tags: ParseProductTags(vars));

            data["createCatalogProduct"] = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(cmd, ct);
        }

        if (ContainsField(mutationText, "updateCatalogProduct"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            var cmd = new UpdateCatalogProductCommand(
                ProductId: productId,
                Name: TryGetString(vars, "name") ?? string.Empty,
                Category: TryGetString(vars, "category") ?? string.Empty,
                UnitPriceHt: TryGetDecimal(vars, "unitPriceHt", 0m),
                BulkTierMinQuantity: TryGetInt(vars, "bulkTierMinQuantity", 10),
                BulkUnitPriceHt: TryGetDecimal(vars, "bulkUnitPriceHt", 0m),
                UnitWeightKg: TryGetDouble(vars, "unitWeightKg", 1.0),
                Description: TryGetString(vars, "description"),
                CategoryId: TryParseOptionalGuid(vars, "categoryId"),
                LowStockThreshold: TryGetInt(vars, "lowStockThreshold", 15),
                VolumePriceTiers: ParseVolumeTiers(vars, "volumePriceTiers"),
                TagIds: ParseGuidList(vars, "tagIds"),
                Tags: ParseProductTags(vars));

            data["updateCatalogProduct"] = await sender.SendCommandAsync<UpdateCatalogProductCommand, CatalogProductDto>(cmd, ct);
        }

        if (ContainsField(mutationText, "archiveCatalogProduct"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            data["archiveCatalogProduct"] = await sender.SendCommandAsync<ArchiveCatalogProductCommand, CatalogProductDto>(
                new ArchiveCatalogProductCommand(productId), ct);
        }

        if (ContainsField(mutationText, "unarchiveCatalogProduct"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            data["unarchiveCatalogProduct"] = await sender.SendCommandAsync<UnarchiveCatalogProductCommand, CatalogProductDto>(
                new UnarchiveCatalogProductCommand(productId), ct);
        }

        if (ContainsField(mutationText, "setProductVolumePriceTiers"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            var tiers = ParseVolumeTiers(vars, "tiers") ?? [];
            data["setProductVolumePriceTiers"] = await sender.SendCommandAsync<SetProductVolumePriceTiersCommand, CatalogProductDto>(
                new SetProductVolumePriceTiersCommand(productId, tiers), ct);
        }
    }

    private async Task ResolveTaxonomyMutationsAsync(
        string mutationText,
        Dictionary<string, JsonElement> vars,
        Dictionary<string, object?> data,
        CancellationToken ct)
    {
        if (ContainsField(mutationText, "createCatalogCategory"))
        {
            data["createCatalogCategory"] = await sender.SendCommandAsync<CreateCatalogCategoryCommand, CatalogCategoryDto>(
                new CreateCatalogCategoryCommand(TryGetString(vars, "name") ?? string.Empty, TryGetString(vars, "description")), ct);
        }

        if (ContainsField(mutationText, "updateCatalogCategory"))
        {
            var categoryId = ParseGuidVar(vars, "categoryId", "id");
            data["updateCatalogCategory"] = await sender.SendCommandAsync<UpdateCatalogCategoryCommand, CatalogCategoryDto>(
                new UpdateCatalogCategoryCommand(categoryId, TryGetString(vars, "name") ?? string.Empty, TryGetString(vars, "description")), ct);
        }

        if (ContainsField(mutationText, "deleteCatalogCategory"))
        {
            var categoryId = ParseGuidVar(vars, "categoryId", "id");
            data["deleteCatalogCategory"] = await sender.SendCommandAsync<DeleteCatalogCategoryCommand, bool>(
                new DeleteCatalogCategoryCommand(categoryId), ct);
        }

        if (ContainsField(mutationText, "createProductTag"))
        {
            data["createProductTag"] = await sender.SendCommandAsync<CreateProductTagCommand, ProductTagDto>(
                new CreateProductTagCommand(
                    TryGetString(vars, "name") ?? string.Empty,
                    TryGetString(vars, "value"),
                    TryGetString(vars, "color") ?? "#0EA5E9"), ct);
        }

        if (ContainsField(mutationText, "deleteProductTag"))
        {
            var tagId = ParseGuidVar(vars, "tagId", "id");
            data["deleteProductTag"] = await sender.SendCommandAsync<DeleteProductTagCommand, bool>(
                new DeleteProductTagCommand(tagId), ct);
        }

        if (ContainsField(mutationText, "assignTagToProduct"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            var tagId = ParseGuidVar(vars, "tagId", "tagId");
            data["assignTagToProduct"] = await sender.SendCommandAsync<AssignTagToProductCommand, CatalogProductDto>(
                new AssignTagToProductCommand(productId, tagId), ct);
        }

        if (ContainsField(mutationText, "removeTagFromProduct"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            var tagId = ParseGuidVar(vars, "tagId", "tagId");
            data["removeTagFromProduct"] = await sender.SendCommandAsync<RemoveTagFromProductCommand, CatalogProductDto>(
                new RemoveTagFromProductCommand(productId, tagId), ct);
        }
    }

    private async Task ResolveStockAndAlertMutationsAsync(
        string mutationText,
        Dictionary<string, JsonElement> vars,
        Dictionary<string, object?> data,
        CancellationToken ct)
    {
        if (ContainsField(mutationText, "restockCatalogProduct"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            var quantityAdded = TryGetInt(vars, "quantityAdded", 0);
            data["restockCatalogProduct"] = await sender.SendCommandAsync<RestockCatalogProductCommand, RestockResultDto>(
                new RestockCatalogProductCommand(productId, quantityAdded), ct);
        }

        if (ContainsField(mutationText, "adjustCatalogStock"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            var newStock = TryGetInt(vars, "newAvailableStock", 0);
            var reason = TryGetString(vars, "reason");
            data["adjustCatalogStock"] = await sender.SendCommandAsync<AdjustCatalogStockCommand, RestockResultDto>(
                new AdjustCatalogStockCommand(productId, newStock, reason), ct);
        }

        if (ContainsField(mutationText, "subscribeStockRefillAlert"))
        {
            var productId = ParseGuidVar(vars, "productId", "id");
            var email = TryGetString(vars, "email") ?? string.Empty;
            var customerId = TryParseOptionalGuid(vars, "customerId");
            data["subscribeStockRefillAlert"] = await sender.SendCommandAsync<SubscribeStockRefillAlertCommand, StockRefillAlertDto>(
                new SubscribeStockRefillAlertCommand(productId, email, customerId), ct);
        }

        if (ContainsField(mutationText, "unsubscribeStockRefillAlert"))
        {
            var alertId = ParseGuidVar(vars, "alertId", "id");
            data["unsubscribeStockRefillAlert"] = await sender.SendCommandAsync<UnsubscribeStockRefillAlertCommand, bool>(
                new UnsubscribeStockRefillAlertCommand(alertId), ct);
        }
    }

    private static Guid ParseGuidVar(Dictionary<string, JsonElement> vars, string primaryKey, string fallbackKey)
    {
        var raw = TryGetString(vars, primaryKey) ?? TryGetString(vars, fallbackKey);
        if (!Guid.TryParse(raw, out var parsed))
        {
            throw new ArgumentException($"Valid GUID variable '{primaryKey}' is required.");
        }

        return parsed;
    }

    private static Guid? TryParseOptionalGuid(Dictionary<string, JsonElement> vars, string key)
    {
        var raw = TryGetString(vars, key);
        return Guid.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static bool ContainsField(string query, string fieldName) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            query,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(fieldName)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

    private static string? TryGetString(Dictionary<string, JsonElement> vars, string key) =>
        vars.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool TryGetBool(Dictionary<string, JsonElement> vars, string key, bool fallback) =>
        vars.TryGetValue(key, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : fallback;

    private static bool? TryGetNullableBool(Dictionary<string, JsonElement> vars, string key) =>
        vars.TryGetValue(key, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : null;

    private static int TryGetInt(Dictionary<string, JsonElement> vars, string key, int fallback) =>
        vars.TryGetValue(key, out var el) && el.TryGetInt32(out var val) ? val : fallback;

    private static decimal TryGetDecimal(Dictionary<string, JsonElement> vars, string key, decimal fallback) =>
        vars.TryGetValue(key, out var el) && el.TryGetDecimal(out var val) ? val : fallback;

    private static double TryGetDouble(Dictionary<string, JsonElement> vars, string key, double fallback) =>
        vars.TryGetValue(key, out var el) && el.TryGetDouble(out var val) ? val : fallback;

    public static IReadOnlyList<Guid>? ParseGuidList(Dictionary<string, JsonElement> vars, string key)
    {
        if (!vars.TryGetValue(key, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<Guid>();
        foreach (var item in arrEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var g))
            {
                list.Add(g);
            }
        }

        return list;
    }

    public static IReadOnlyList<VolumePriceTierInput>? ParseVolumeTiers(Dictionary<string, JsonElement> vars, string key)
    {
        if (!vars.TryGetValue(key, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<VolumePriceTierInput>();
        foreach (var item in arrEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var minQty = item.TryGetProperty("minQuantity", out var mq) && mq.TryGetInt32(out var qv) ? qv : 0;
                var price = item.TryGetProperty("unitPriceHt", out var up) && up.TryGetDecimal(out var pv) ? pv : 0m;
                list.Add(new VolumePriceTierInput(minQty, price));
            }
        }

        return list;
    }

    public static IReadOnlyList<ProductTagInput>? ParseProductTags(Dictionary<string, JsonElement> vars)
    {
        if (!vars.TryGetValue("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<ProductTagInput>();
        foreach (var item in tagsEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var tagName = item.GetString();
                if (!string.IsNullOrWhiteSpace(tagName))
                {
                    list.Add(new ProductTagInput(tagName));
                }
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var value = item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var color = item.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "#0EA5E9";
                list.Add(new ProductTagInput(name, value, color));
            }
        }

        return list;
    }
}
