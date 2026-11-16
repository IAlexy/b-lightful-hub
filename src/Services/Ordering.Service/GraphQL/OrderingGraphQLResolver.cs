namespace Ordering.Service.GraphQL;

using System.Text.Json;
using B2B.BuildingBlocks.Cqrs;

public sealed record OrderingGraphQLRequest(
    string Query,
    string? OperationName = null,
    Dictionary<string, JsonElement>? Variables = null);

public sealed record OrderingGraphQLError(string Message);

public sealed record OrderingGraphQLResponse(
    Dictionary<string, object?>? Data,
    IReadOnlyList<OrderingGraphQLError>? Errors = null);

/// <summary>
/// Ordering Microservice GraphQL Subgraph Resolver handling:
/// - Mutation: createOrder / createPurchaseOrder (Customer + Order + OrderLines via EF Core & AutoMapper)
/// - Query 1:  orders(buyerVatNumber: String) (Orders list with Customer & OrderLines)
/// - Query 2:  orderById(id: ID!) (Specific order by ID with Customer & OrderLines)
/// </summary>
public sealed class OrderingGraphQLResolver(ISender sender)
{
    public const string SubgraphSdl = """
        type Customer {
          id: ID!
          companyName: String!
          vatNumber: String!
          contactEmail: String!
          billingAddress: String!
        }

        type OrderLine {
          id: ID!
          sku: String!
          productName: String!
          quantity: Int!
          unitPriceHt: Float!
          lineTotalHt: Float!
          unitWeightKg: Float!
        }

        type Order {
          id: ID!
          orderNumber: String!
          customerId: ID!
          customer: Customer!
          buyerCompanyName: String!
          buyerVatNumber: String!
          paymentTermsDays: Int!
          shippingDestination: String!
          status: String!
          createdAtUtc: String!
          totalAmountHt: Float!
          totalUnits: Int!
          lines: [OrderLine!]!
        }

        input OrderLineInput {
          sku: String!
          quantity: Int!
        }

        type Query {
          orders(buyerVatNumber: String): [Order!]!
          orderById(id: ID!): Order
        }

        type Mutation {
          createOrder(
            buyerCompanyName: String!
            buyerVatNumber: String!
            contactEmail: String
            paymentTermsDays: Int!
            shippingDestination: String!
            autoConfirm: Boolean = true
            items: [OrderLineInput!]!
          ): Order!
        }
        """;

    public async Task<OrderingGraphQLResponse> ExecuteAsync(OrderingGraphQLRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            return new OrderingGraphQLResponse(null, [new OrderingGraphQLError("GraphQL query cannot be empty.")]);
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
            return new OrderingGraphQLResponse(null, [new OrderingGraphQLError(ex.Message)]);
        }
    }

    public Task<PurchaseOrderDto> ResolveCreateOrderMutationAsync(
        Dictionary<string, JsonElement> vars,
        CancellationToken ct = default)
    {
        var items = ParseOrderLines(vars);
        var command = new CreatePurchaseOrderCommand(
            BuyerCompanyName: TryGetString(vars, "buyerCompanyName") ?? "Enterprise Corp SAS",
            BuyerVatNumber: TryGetString(vars, "buyerVatNumber") ?? "FR9988776655",
            PaymentTermsDays: TryGetInt(vars, "paymentTermsDays", 30),
            ShippingDestination: TryGetString(vars, "shippingDestination") ?? "Lyon Hub Logistique, France",
            Items: items,
            AutoConfirm: TryGetBool(vars, "autoConfirm", true),
            ContactEmail: TryGetString(vars, "contactEmail"));

        return sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(command, ct);
    }

    public Task<IReadOnlyList<PurchaseOrderDto>> ResolveOrdersListQueryAsync(
        Dictionary<string, JsonElement> vars,
        CancellationToken ct = default)
    {
        var buyerVatNumber = TryGetString(vars, "buyerVatNumber");
        return sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(
            new GetPurchaseOrdersQuery(buyerVatNumber), ct);
    }

    public async Task<PurchaseOrderDto?> ResolveOrderByIdQueryAsync(
        Dictionary<string, JsonElement> vars,
        CancellationToken ct = default)
    {
        var idStr = TryGetString(vars, "id") ?? TryGetString(vars, "orderId");
        if (!Guid.TryParse(idStr, out var orderId))
        {
            return null;
        }

        return await sender.SendQueryAsync<GetPurchaseOrderByIdQuery, PurchaseOrderDto?>(
            new GetPurchaseOrderByIdQuery(orderId), ct);
    }

    private async Task<OrderingGraphQLResponse> ExecuteQueryAsync(
        string queryText,
        Dictionary<string, JsonElement> vars,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        if (ContainsField(queryText, "__schema"))
        {
            data["__schema"] = new { sdl = SubgraphSdl };
        }

        if (ContainsField(queryText, "orders"))
        {
            data["orders"] = await ResolveOrdersListQueryAsync(vars, ct);
        }

        if (ContainsField(queryText, "orderById"))
        {
            data["orderById"] = await ResolveOrderByIdQueryAsync(vars, ct);
        }

        if (data.Count == 0)
        {
            return new OrderingGraphQLResponse(null, [new OrderingGraphQLError("Unknown Ordering GraphQL query field.")]);
        }

        return new OrderingGraphQLResponse(data);
    }

    private async Task<OrderingGraphQLResponse> ExecuteMutationAsync(
        string mutationText,
        Dictionary<string, JsonElement> vars,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        if (ContainsField(mutationText, "createOrder"))
        {
            data["createOrder"] = await ResolveCreateOrderMutationAsync(vars, ct);
        }

        if (ContainsField(mutationText, "createPurchaseOrder"))
        {
            data["createPurchaseOrder"] = await ResolveCreateOrderMutationAsync(vars, ct);
        }

        if (data.Count == 0)
        {
            return new OrderingGraphQLResponse(null, [new OrderingGraphQLError("Unknown Ordering GraphQL mutation field.")]);
        }

        return new OrderingGraphQLResponse(data);
    }

    private static bool ContainsField(string query, string fieldName) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            query,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(fieldName)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

    private static string? TryGetString(Dictionary<string, JsonElement> vars, string key) =>
        vars.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int TryGetInt(Dictionary<string, JsonElement> vars, string key, int fallback) =>
        vars.TryGetValue(key, out var el) && el.TryGetInt32(out var val) ? val : fallback;

    private static bool TryGetBool(Dictionary<string, JsonElement> vars, string key, bool fallback) =>
        vars.TryGetValue(key, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : fallback;

    private static IReadOnlyList<OrderLineRequest> ParseOrderLines(Dictionary<string, JsonElement> vars)
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
