namespace Customer.Service;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Customer.Service.DataModels;
using Customer.Service.Entities;
using Customer.Service.Persistence;
using Delivery.Service;
using Invoicing.Service;
using Microsoft.EntityFrameworkCore;
using Ordering.Service;

// ============================================================================
// 1. CQRS COMMANDS & QUERIES FOR CUSTOMER MICROSERVICE
// ============================================================================

/// <summary>
/// Command to create a new B2B Customer with SIREN/SIRET, Invoicing & Delivery addresses
/// (with UseInvoicingAddressForDelivery automatic copy), and free custom tags.
/// </summary>
public sealed record CreateB2BCustomerCommand(
    string Name,
    string SirenSiret,
    string InvoicingAddress,
    string? DeliveryAddress = null,
    bool UseInvoicingAddressForDelivery = false,
    IReadOnlyList<CustomTagInput>? Tags = null,
    string? ContactEmail = null) : ICommand<B2BCustomerDto>;

/// <summary>
/// Command to update an existing B2B Customer, its addresses, and its custom tags.
/// </summary>
public sealed record UpdateB2BCustomerCommand(
    Guid CustomerId,
    string Name,
    string SirenSiret,
    string InvoicingAddress,
    string? DeliveryAddress = null,
    bool UseInvoicingAddressForDelivery = false,
    IReadOnlyList<CustomTagInput>? Tags = null,
    string? ContactEmail = null) : ICommand<B2BCustomerDto>;

/// <summary>
/// Command to delete an existing B2B Customer and its custom tags.
/// </summary>
public sealed record DeleteB2BCustomerCommand(Guid CustomerId) : ICommand<bool>;

/// <summary>
/// Query to list all B2B Customers with optional text search or custom tag filtering.
/// </summary>
public sealed record GetB2BCustomersQuery(
    string? Search = null,
    string? Tag = null) : IQuery<IReadOnlyList<B2BCustomerDto>>;

/// <summary>
/// Query to retrieve a specific B2B Customer by ID.
/// </summary>
public sealed record GetB2BCustomerByIdQuery(Guid CustomerId) : IQuery<B2BCustomerDto?>;

/// <summary>
/// Query to retrieve all Purchase Orders belonging to a specific B2B Customer.
/// </summary>
public sealed record GetCustomerOrdersQuery(Guid CustomerId) : IQuery<IReadOnlyList<PurchaseOrderDto>>;

/// <summary>
/// Query to retrieve all B2B Invoices belonging to a specific B2B Customer.
/// </summary>
public sealed record GetCustomerInvoicesQuery(Guid CustomerId) : IQuery<IReadOnlyList<B2BInvoiceDto>>;

/// <summary>
/// Query to retrieve all Freight Deliveries (Shipments) belonging to a specific B2B Customer.
/// </summary>
public sealed record GetCustomerDeliveriesQuery(Guid CustomerId) : IQuery<IReadOnlyList<B2BShipmentDto>>;

/// <summary>
/// Query to retrieve all Favorite Products for a specific B2B Customer
/// (throws <see cref="CustomerFavoriteProductsNotImplementedException"/> until Catalog persistence is implemented).
/// </summary>
public sealed record GetCustomerFavoriteProductsQuery(Guid CustomerId) : IQuery<IReadOnlyList<CustomerFavoriteProductDto>>;

// ============================================================================
// 2. CUSTOMER DOMAIN & ORCHESTRATION SERVICE
// ============================================================================

public interface ICustomerService
{
    Task<B2BCustomerDto> CreateCustomerAsync(CustomerAccountDataModel customerModel, CancellationToken ct = default);
    Task<IReadOnlyList<B2BCustomerDto>> GetCustomersAsync(string? search = null, string? tag = null, CancellationToken ct = default);
    Task<B2BCustomerDto?> GetCustomerByIdAsync(Guid customerId, CancellationToken ct = default);
    Task<B2BCustomerDto> UpdateCustomerAsync(CustomerAccountDataModel customerModel, CancellationToken ct = default);
    Task<bool> DeleteCustomerAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<PurchaseOrderDto>> GetCustomerOrdersAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<B2BInvoiceDto>> GetCustomerInvoicesAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<B2BShipmentDto>> GetCustomerDeliveriesAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerFavoriteProductDto>> GetCustomerFavoriteProductsAsync(Guid customerId, CancellationToken ct = default);
}

public sealed class CustomerCrudService(
    CustomerDbContext dbContext,
    IMapper autoMapper,
    ISender sender) : ICustomerService
{
    public async Task<B2BCustomerDto> CreateCustomerAsync(
        CustomerAccountDataModel customerModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(customerModel);
        customerModel.ApplyBusinessRulesAndValidate();

        var duplicateExists = await dbContext.Customers
            .AnyAsync(c => c.SirenSiret == customerModel.SirenSiret, ct);

        if (duplicateExists)
        {
            throw new CustomerDomainException(
                "DUPLICATE_SIREN_SIRET",
                $"A customer with SIREN/SIRET '{customerModel.SirenSiret}' already exists.");
        }

        var entity = autoMapper.Map<CustomerAccountDataModel, CustomerProfileEntity>(customerModel);
        foreach (var tag in entity.Tags)
        {
            tag.CustomerId = entity.Id;
        }

        dbContext.Customers.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        var savedModel = autoMapper.Map<CustomerProfileEntity, CustomerAccountDataModel>(entity);
        return autoMapper.Map<CustomerAccountDataModel, B2BCustomerDto>(savedModel);
    }

    public async Task<IReadOnlyList<B2BCustomerDto>> GetCustomersAsync(
        string? search = null,
        string? tag = null,
        CancellationToken ct = default)
    {
        var entities = await dbContext.Customers
            .AsNoTracking()
            .Include(c => c.Tags)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        IEnumerable<CustomerProfileEntity> filtered = entities;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(c =>
                c.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.SirenSiret.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.InvoicingAddress.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.DeliveryAddress.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var tagTerm = tag.Trim();
            filtered = filtered.Where(c =>
                c.Tags.Any(t =>
                    t.Name.Equals(tagTerm, StringComparison.OrdinalIgnoreCase) ||
                    (t.Value is not null && t.Value.Contains(tagTerm, StringComparison.OrdinalIgnoreCase))));
        }

        var dataModels = autoMapper.Map<IEnumerable<CustomerProfileEntity>, List<CustomerAccountDataModel>>(filtered);
        return autoMapper.Map<List<CustomerAccountDataModel>, List<B2BCustomerDto>>(dataModels);
    }

    public async Task<B2BCustomerDto?> GetCustomerByIdAsync(Guid customerId, CancellationToken ct = default)
    {
        var entity = await dbContext.Customers
            .AsNoTracking()
            .Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);

        if (entity is null)
        {
            return null;
        }

        var dataModel = autoMapper.Map<CustomerProfileEntity, CustomerAccountDataModel>(entity);
        return autoMapper.Map<CustomerAccountDataModel, B2BCustomerDto>(dataModel);
    }

    public async Task<B2BCustomerDto> UpdateCustomerAsync(
        CustomerAccountDataModel customerModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(customerModel);
        customerModel.ApplyBusinessRulesAndValidate();

        var existing = await dbContext.Customers
            .Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == customerModel.Id, ct);

        if (existing is null)
        {
            throw new CustomerDomainException(
                "CUSTOMER_NOT_FOUND",
                $"Customer {customerModel.Id} was not found.");
        }

        var duplicateSiren = await dbContext.Customers
            .AnyAsync(c => c.Id != customerModel.Id && c.SirenSiret == customerModel.SirenSiret, ct);

        if (duplicateSiren)
        {
            throw new CustomerDomainException(
                "DUPLICATE_SIREN_SIRET",
                $"Another customer with SIREN/SIRET '{customerModel.SirenSiret}' already exists.");
        }

        existing.Name = customerModel.Name;
        existing.SirenSiret = customerModel.SirenSiret;
        existing.InvoicingAddress = customerModel.InvoicingAddress;
        existing.DeliveryAddress = customerModel.DeliveryAddress;
        existing.UseInvoicingAddressForDelivery = customerModel.UseInvoicingAddressForDelivery;
        existing.ContactEmail = customerModel.ContactEmail;
        existing.UpdatedAtUtc = DateTime.UtcNow;

        // Replace custom tags
        dbContext.CustomTags.RemoveRange(existing.Tags);
        existing.Tags.Clear();

        foreach (var tagModel in customerModel.Tags)
        {
            var tagEntity = autoMapper.Map<CustomTagDataModel, CustomerCustomTagEntity>(tagModel);
            tagEntity.CustomerId = existing.Id;
            existing.Tags.Add(tagEntity);
            dbContext.CustomTags.Add(tagEntity);
        }

        await dbContext.SaveChangesAsync(ct);

        var updatedModel = autoMapper.Map<CustomerProfileEntity, CustomerAccountDataModel>(existing);
        return autoMapper.Map<CustomerAccountDataModel, B2BCustomerDto>(updatedModel);
    }

    public async Task<bool> DeleteCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        var existing = await dbContext.Customers
            .Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);

        if (existing is null)
        {
            throw new CustomerDomainException(
                "CUSTOMER_NOT_FOUND",
                $"Customer {customerId} was not found.");
        }

        dbContext.Customers.Remove(existing);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<PurchaseOrderDto>> GetCustomerOrdersAsync(Guid customerId, CancellationToken ct = default)
    {
        var customer = await RequireCustomerEntityAsync(customerId, ct);
        var allOrders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(
            new GetPurchaseOrdersQuery(), ct);

        return allOrders
            .Where(o => MatchesCustomer(o.CustomerId, o.BuyerVatNumber, o.BuyerCompanyName, customer))
            .ToList();
    }

    public async Task<IReadOnlyList<B2BInvoiceDto>> GetCustomerInvoicesAsync(Guid customerId, CancellationToken ct = default)
    {
        var customer = await RequireCustomerEntityAsync(customerId, ct);
        var customerOrders = await GetCustomerOrdersAsync(customerId, ct);
        var customerOrderIds = customerOrders.Select(o => o.Id).ToHashSet();

        var allInvoices = await sender.SendQueryAsync<GetInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>(
            new GetInvoicesQuery(), ct);

        return allInvoices
            .Where(inv =>
                customerOrderIds.Contains(inv.OrderId) ||
                MatchesCustomer(null, inv.BuyerVatNumber, inv.BuyerCompanyName, customer))
            .ToList();
    }

    public async Task<IReadOnlyList<B2BShipmentDto>> GetCustomerDeliveriesAsync(Guid customerId, CancellationToken ct = default)
    {
        var customer = await RequireCustomerEntityAsync(customerId, ct);
        var customerOrders = await GetCustomerOrdersAsync(customerId, ct);
        var customerOrderIds = customerOrders.Select(o => o.Id).ToHashSet();

        var allShipments = await sender.SendQueryAsync<GetShipmentsQuery, IReadOnlyList<B2BShipmentDto>>(
            new GetShipmentsQuery(), ct);

        return allShipments
            .Where(s =>
                customerOrderIds.Contains(s.OrderId) ||
                string.Equals(s.DestinationAddress, customer.DeliveryAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<IReadOnlyList<CustomerFavoriteProductDto>> GetCustomerFavoriteProductsAsync(
        Guid customerId,
        CancellationToken ct = default)
    {
        var customer = await RequireCustomerEntityAsync(customerId, ct);
        throw new CustomerFavoriteProductsNotImplementedException(customer.Id, customer.Name);
    }

    private async Task<CustomerProfileEntity> RequireCustomerEntityAsync(Guid customerId, CancellationToken ct)
    {
        var customer = await dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);

        if (customer is null)
        {
            throw new CustomerDomainException(
                "CUSTOMER_NOT_FOUND",
                $"Customer {customerId} was not found.");
        }

        return customer;
    }

    private static bool MatchesCustomer(
        Guid? orderCustomerId,
        string? vatOrSiren,
        string? companyName,
        CustomerProfileEntity customer)
    {
        if (orderCustomerId.HasValue && orderCustomerId.Value == customer.Id)
        {
            return true;
        }

        var normalizedIdentifier = CustomerAccountDataModel.NormalizeSirenSiret(vatOrSiren);
        if (!string.IsNullOrEmpty(normalizedIdentifier) &&
            (string.Equals(normalizedIdentifier, customer.SirenSiret, StringComparison.OrdinalIgnoreCase) ||
             normalizedIdentifier.EndsWith(customer.SirenSiret, StringComparison.OrdinalIgnoreCase) ||
             customer.SirenSiret.EndsWith(normalizedIdentifier, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(companyName) &&
               string.Equals(companyName.Trim(), customer.Name, StringComparison.OrdinalIgnoreCase);
    }
}

// ============================================================================
// 3. CQRS COMMAND & QUERY HANDLERS
// ============================================================================

public sealed class CreateB2BCustomerCommandHandler(
    ICustomerService customerService,
    IMapper autoMapper) : ICommandHandler<CreateB2BCustomerCommand, B2BCustomerDto>
{
    public Task<B2BCustomerDto> HandleAsync(CreateB2BCustomerCommand command, CancellationToken ct = default)
    {
        var dataModel = autoMapper.Map<CreateB2BCustomerCommand, CustomerAccountDataModel>(command);
        return customerService.CreateCustomerAsync(dataModel, ct);
    }
}

public sealed class UpdateB2BCustomerCommandHandler(
    ICustomerService customerService,
    IMapper autoMapper) : ICommandHandler<UpdateB2BCustomerCommand, B2BCustomerDto>
{
    public Task<B2BCustomerDto> HandleAsync(UpdateB2BCustomerCommand command, CancellationToken ct = default)
    {
        var dataModel = autoMapper.Map<UpdateB2BCustomerCommand, CustomerAccountDataModel>(command);
        return customerService.UpdateCustomerAsync(dataModel, ct);
    }
}

public sealed class DeleteB2BCustomerCommandHandler(
    ICustomerService customerService) : ICommandHandler<DeleteB2BCustomerCommand, bool>
{
    public Task<bool> HandleAsync(DeleteB2BCustomerCommand command, CancellationToken ct = default) =>
        customerService.DeleteCustomerAsync(command.CustomerId, ct);
}

public sealed class GetB2BCustomersQueryHandler(
    ICustomerService customerService) : IQueryHandler<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>
{
    public Task<IReadOnlyList<B2BCustomerDto>> HandleAsync(GetB2BCustomersQuery query, CancellationToken ct = default) =>
        customerService.GetCustomersAsync(query.Search, query.Tag, ct);
}

public sealed class GetB2BCustomerByIdQueryHandler(
    ICustomerService customerService) : IQueryHandler<GetB2BCustomerByIdQuery, B2BCustomerDto?>
{
    public Task<B2BCustomerDto?> HandleAsync(GetB2BCustomerByIdQuery query, CancellationToken ct = default) =>
        customerService.GetCustomerByIdAsync(query.CustomerId, ct);
}

public sealed class GetCustomerOrdersQueryHandler(
    ICustomerService customerService) : IQueryHandler<GetCustomerOrdersQuery, IReadOnlyList<PurchaseOrderDto>>
{
    public Task<IReadOnlyList<PurchaseOrderDto>> HandleAsync(GetCustomerOrdersQuery query, CancellationToken ct = default) =>
        customerService.GetCustomerOrdersAsync(query.CustomerId, ct);
}

public sealed class GetCustomerInvoicesQueryHandler(
    ICustomerService customerService) : IQueryHandler<GetCustomerInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>
{
    public Task<IReadOnlyList<B2BInvoiceDto>> HandleAsync(GetCustomerInvoicesQuery query, CancellationToken ct = default) =>
        customerService.GetCustomerInvoicesAsync(query.CustomerId, ct);
}

public sealed class GetCustomerDeliveriesQueryHandler(
    ICustomerService customerService) : IQueryHandler<GetCustomerDeliveriesQuery, IReadOnlyList<B2BShipmentDto>>
{
    public Task<IReadOnlyList<B2BShipmentDto>> HandleAsync(GetCustomerDeliveriesQuery query, CancellationToken ct = default) =>
        customerService.GetCustomerDeliveriesAsync(query.CustomerId, ct);
}

public sealed class GetCustomerFavoriteProductsQueryHandler(
    ICustomerService customerService) : IQueryHandler<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>
{
    public Task<IReadOnlyList<CustomerFavoriteProductDto>> HandleAsync(
        GetCustomerFavoriteProductsQuery query,
        CancellationToken ct = default) =>
        customerService.GetCustomerFavoriteProductsAsync(query.CustomerId, ct);
}
