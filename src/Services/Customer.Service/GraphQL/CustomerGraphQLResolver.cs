namespace Customer.Service.GraphQL;

using System.Text.Json;
using B2B.BuildingBlocks.Cqrs;
using Delivery.Service;
using Invoicing.Service;
using Ordering.Service;

public sealed record CustomerGraphQLRequest(
    string Query,
    string? OperationName = null,
    Dictionary<string, JsonElement>? Variables = null);

public sealed record CustomerGraphQLError(string Message, string? ErrorCode = null);

public sealed record CustomerGraphQLResponse(
    Dictionary<string, object?>? Data,
    IReadOnlyList<CustomerGraphQLError>? Errors = null);

/// <summary>
/// Customer Microservice GraphQL Subgraph Resolver supporting:
/// - Queries: customers(search, tag), customerById(id), tags(search), tagById(id),
///            customerOrders(customerId), customerInvoices(customerId),
///            customerDeliveries(customerId), customerFavoriteProducts(customerId)
/// - Mutations: createCustomer, updateCustomer, deleteCustomer,
///              createTag, deleteTag, assignTagToCustomer, removeTagFromCustomer
/// </summary>
public sealed class CustomerGraphQLResolver(ISender sender)
{
    public const string SubgraphSdl = """
        type CustomTag {
          id: ID!
          name: String!
          value: String
          color: String!
        }

        input CustomTagInput {
          name: String!
          value: String
          color: String = "#7C3AED"
        }

        type B2BCustomer {
          id: ID!
          name: String!
          sirenSiret: String!
          invoicingAddress: String!
          deliveryAddress: String!
          useInvoicingAddressForDelivery: Boolean!
          contactEmail: String
          phoneNumber: String
          tags: [CustomTag!]!
          createdAtUtc: String!
          updatedAtUtc: String!
        }

        type Query {
          customers(search: String, tag: String): [B2BCustomer!]!
          customerById(id: ID!): B2BCustomer
          tags(search: String): [CustomTag!]!
          tagById(id: ID!): CustomTag
          customerOrders(customerId: ID!): [PurchaseOrder!]!
          customerInvoices(customerId: ID!): [B2BInvoice!]!
          customerDeliveries(customerId: ID!): [B2BShipment!]!
          customerFavoriteProducts(customerId: ID!): [CustomerFavoriteProduct!]!
        }

        type Mutation {
          createCustomer(
            name: String!
            sirenSiret: String!
            invoicingAddress: String!
            deliveryAddress: String
            useInvoicingAddressForDelivery: Boolean = false
            contactEmail: String
            phoneNumber: String
            tagIds: [ID!]
            tags: [CustomTagInput!]
          ): B2BCustomer!
          updateCustomer(
            customerId: ID!
            name: String!
            sirenSiret: String!
            invoicingAddress: String!
            deliveryAddress: String
            useInvoicingAddressForDelivery: Boolean = false
            contactEmail: String
            phoneNumber: String
            tagIds: [ID!]
            tags: [CustomTagInput!]
          ): B2BCustomer!
          deleteCustomer(customerId: ID!): Boolean!
          createTag(name: String!, value: String, color: String = "#7C3AED"): CustomTag!
          deleteTag(tagId: ID!): Boolean!
          assignTagToCustomer(customerId: ID!, tagId: ID!): B2BCustomer!
          removeTagFromCustomer(customerId: ID!, tagId: ID!): B2BCustomer!
        }
        """;

    public async Task<CustomerGraphQLResponse> ExecuteAsync(
        CustomerGraphQLRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            return new CustomerGraphQLResponse(null, [new CustomerGraphQLError("GraphQL query cannot be empty.")]);
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
        catch (DataModels.CustomerFavoriteProductsNotImplementedException ex)
        {
            return new CustomerGraphQLResponse(null, [new CustomerGraphQLError(ex.Message, ex.ErrorCode)]);
        }
        catch (DataModels.CustomerDomainException ex)
        {
            return new CustomerGraphQLResponse(null, [new CustomerGraphQLError(ex.Message, ex.ErrorCode)]);
        }
        catch (Exception ex)
        {
            return new CustomerGraphQLResponse(null, [new CustomerGraphQLError(ex.Message)]);
        }
    }

    private async Task<CustomerGraphQLResponse> ExecuteQueryAsync(
        string queryText,
        Dictionary<string, JsonElement> vars,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        if (ContainsField(queryText, "__schema"))
        {
            data["__schema"] = new { sdl = SubgraphSdl };
        }

        if (ContainsField(queryText, "customers"))
        {
            var search = TryGetString(vars, "search");
            var tag = TryGetString(vars, "tag");
            data["customers"] = await sender.SendQueryAsync<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>(
                new GetB2BCustomersQuery(search, tag), ct);
        }

        if (ContainsField(queryText, "customerById"))
        {
            var customerId = ParseGuidVar(vars, "id", "customerId");
            data["customerById"] = await sender.SendQueryAsync<GetB2BCustomerByIdQuery, B2BCustomerDto?>(
                new GetB2BCustomerByIdQuery(customerId), ct);
        }

        if (ContainsRootFieldOnly(queryText, "tags"))
        {
            var search = TryGetString(vars, "search");
            data["tags"] = await sender.SendQueryAsync<GetCustomTagsQuery, IReadOnlyList<CustomTagDto>>(
                new GetCustomTagsQuery(search), ct);
        }

        if (ContainsField(queryText, "tagById"))
        {
            var tagId = ParseGuidVar(vars, "tagId", "id");
            data["tagById"] = await sender.SendQueryAsync<GetCustomTagByIdQuery, CustomTagDto?>(
                new GetCustomTagByIdQuery(tagId), ct);
        }

        if (ContainsField(queryText, "customerOrders"))
        {
            var customerId = ParseGuidVar(vars, "customerId", "id");
            data["customerOrders"] = await sender.SendQueryAsync<GetCustomerOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(
                new GetCustomerOrdersQuery(customerId), ct);
        }

        if (ContainsField(queryText, "customerInvoices"))
        {
            var customerId = ParseGuidVar(vars, "customerId", "id");
            data["customerInvoices"] = await sender.SendQueryAsync<GetCustomerInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>(
                new GetCustomerInvoicesQuery(customerId), ct);
        }

        if (ContainsField(queryText, "customerDeliveries"))
        {
            var customerId = ParseGuidVar(vars, "customerId", "id");
            data["customerDeliveries"] = await sender.SendQueryAsync<GetCustomerDeliveriesQuery, IReadOnlyList<B2BShipmentDto>>(
                new GetCustomerDeliveriesQuery(customerId), ct);
        }

        if (ContainsField(queryText, "customerFavoriteProducts"))
        {
            var customerId = ParseGuidVar(vars, "customerId", "id");
            data["customerFavoriteProducts"] = await sender.SendQueryAsync<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>(
                new GetCustomerFavoriteProductsQuery(customerId), ct);
        }

        if (data.Count == 0)
        {
            return new CustomerGraphQLResponse(null, [new CustomerGraphQLError("Unknown Customer GraphQL query field.")]);
        }

        return new CustomerGraphQLResponse(data);
    }

    private async Task<CustomerGraphQLResponse> ExecuteMutationAsync(
        string mutationText,
        Dictionary<string, JsonElement> vars,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        if (ContainsField(mutationText, "createCustomer"))
        {
            var cmd = new CreateB2BCustomerCommand(
                Name: TryGetString(vars, "name") ?? string.Empty,
                SirenSiret: TryGetString(vars, "sirenSiret") ?? string.Empty,
                InvoicingAddress: TryGetString(vars, "invoicingAddress") ?? string.Empty,
                DeliveryAddress: TryGetString(vars, "deliveryAddress"),
                UseInvoicingAddressForDelivery: TryGetBool(vars, "useInvoicingAddressForDelivery", false),
                ContactEmail: TryGetString(vars, "contactEmail"),
                PhoneNumber: TryGetString(vars, "phoneNumber"),
                TagIds: ParseGuidList(vars, "tagIds"),
                Tags: ParseTags(vars));

            data["createCustomer"] = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(cmd, ct);
        }

        if (ContainsField(mutationText, "updateCustomer"))
        {
            var customerId = ParseGuidVar(vars, "customerId", "id");
            var cmd = new UpdateB2BCustomerCommand(
                CustomerId: customerId,
                Name: TryGetString(vars, "name") ?? string.Empty,
                SirenSiret: TryGetString(vars, "sirenSiret") ?? string.Empty,
                InvoicingAddress: TryGetString(vars, "invoicingAddress") ?? string.Empty,
                DeliveryAddress: TryGetString(vars, "deliveryAddress"),
                UseInvoicingAddressForDelivery: TryGetBool(vars, "useInvoicingAddressForDelivery", false),
                ContactEmail: TryGetString(vars, "contactEmail"),
                PhoneNumber: TryGetString(vars, "phoneNumber"),
                TagIds: ParseGuidList(vars, "tagIds"),
                Tags: ParseTags(vars));

            data["updateCustomer"] = await sender.SendCommandAsync<UpdateB2BCustomerCommand, B2BCustomerDto>(cmd, ct);
        }

        if (ContainsField(mutationText, "deleteCustomer"))
        {
            var customerId = ParseGuidVar(vars, "customerId", "id");
            data["deleteCustomer"] = await sender.SendCommandAsync<DeleteB2BCustomerCommand, bool>(
                new DeleteB2BCustomerCommand(customerId), ct);
        }

        if (ContainsField(mutationText, "createTag"))
        {
            var cmd = new CreateCustomTagCommand(
                Name: TryGetString(vars, "name") ?? string.Empty,
                Value: TryGetString(vars, "value"),
                Color: TryGetString(vars, "color") ?? "#7C3AED");

            data["createTag"] = await sender.SendCommandAsync<CreateCustomTagCommand, CustomTagDto>(cmd, ct);
        }

        if (ContainsField(mutationText, "deleteTag"))
        {
            var tagId = ParseGuidVar(vars, "tagId", "id");
            data["deleteTag"] = await sender.SendCommandAsync<DeleteCustomTagCommand, bool>(
                new DeleteCustomTagCommand(tagId), ct);
        }

        if (ContainsField(mutationText, "assignTagToCustomer"))
        {
            var customerId = ParseGuidVar(vars, "customerId", "id");
            var tagId = ParseGuidVar(vars, "tagId", "tagId");
            data["assignTagToCustomer"] = await sender.SendCommandAsync<AssignTagToCustomerCommand, B2BCustomerDto>(
                new AssignTagToCustomerCommand(customerId, tagId), ct);
        }

        if (ContainsField(mutationText, "removeTagFromCustomer"))
        {
            var customerId = ParseGuidVar(vars, "customerId", "id");
            var tagId = ParseGuidVar(vars, "tagId", "tagId");
            data["removeTagFromCustomer"] = await sender.SendCommandAsync<RemoveTagFromCustomerCommand, B2BCustomerDto>(
                new RemoveTagFromCustomerCommand(customerId, tagId), ct);
        }

        if (data.Count == 0)
        {
            return new CustomerGraphQLResponse(null, [new CustomerGraphQLError("Unknown Customer GraphQL mutation field.")]);
        }

        return new CustomerGraphQLResponse(data);
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

    private static bool ContainsField(string query, string fieldName) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            query,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(fieldName)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

    private static bool ContainsRootFieldOnly(string query, string fieldName) =>
        !ContainsField(query, "customers") && !ContainsField(query, "customerById") && ContainsField(query, fieldName);

    private static string? TryGetString(Dictionary<string, JsonElement> vars, string key) =>
        vars.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool TryGetBool(Dictionary<string, JsonElement> vars, string key, bool fallback) =>
        vars.TryGetValue(key, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : fallback;

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

    public static IReadOnlyList<CustomTagInput>? ParseTags(Dictionary<string, JsonElement> vars)
    {
        if (!vars.TryGetValue("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<CustomTagInput>();
        foreach (var item in tagsEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var tagName = item.GetString();
                if (!string.IsNullOrWhiteSpace(tagName))
                {
                    list.Add(new CustomTagInput(tagName));
                }
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var value = item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var color = item.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "#7C3AED";
                list.Add(new CustomTagInput(name, value, color));
            }
        }

        return list;
    }
}
