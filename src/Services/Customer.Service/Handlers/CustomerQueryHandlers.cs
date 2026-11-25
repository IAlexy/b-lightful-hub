namespace Customer.Service.Handlers;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Customer.Service.DataModels;
using Customer.Service.Services;
using Delivery.Service;
using Invoicing.Service;
using Ordering.Service;

public sealed class GetB2BCustomersQueryHandler(
    ICustomerService customerService,
    IMapper autoMapper) : IQueryHandler<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>
{
    public Task<IReadOnlyList<B2BCustomerDto>> HandleAsync(GetB2BCustomersQuery query, CancellationToken ct = default)
    {
        var filterModel = autoMapper.Map<GetB2BCustomersQuery, CustomerFilterDataModel>(query);
        return customerService.GetCustomersAsync(filterModel.Search, filterModel.Tag, ct);
    }
}

public sealed class GetB2BCustomerByIdQueryHandler(
    ICustomerService customerService,
    IMapper autoMapper) : IQueryHandler<GetB2BCustomerByIdQuery, B2BCustomerDto?>
{
    public Task<B2BCustomerDto?> HandleAsync(GetB2BCustomerByIdQuery query, CancellationToken ct = default)
    {
        var lookup = autoMapper.Map<GetB2BCustomerByIdQuery, CustomerIdLookupDataModel>(query);
        return customerService.GetCustomerByIdAsync(lookup.CustomerId, ct);
    }
}

public sealed class GetCustomTagsQueryHandler(
    ICustomerService customerService) : IQueryHandler<GetCustomTagsQuery, IReadOnlyList<CustomTagDto>>
{
    public Task<IReadOnlyList<CustomTagDto>> HandleAsync(GetCustomTagsQuery query, CancellationToken ct = default) =>
        customerService.GetCustomTagsAsync(query.Search, ct);
}

public sealed class GetCustomTagByIdQueryHandler(
    ICustomerService customerService) : IQueryHandler<GetCustomTagByIdQuery, CustomTagDto?>
{
    public Task<CustomTagDto?> HandleAsync(GetCustomTagByIdQuery query, CancellationToken ct = default) =>
        customerService.GetCustomTagByIdAsync(query.TagId, ct);
}

public sealed class GetCustomerOrdersQueryHandler(
    ICustomerService customerService,
    IMapper autoMapper) : IQueryHandler<GetCustomerOrdersQuery, IReadOnlyList<PurchaseOrderDto>>
{
    public Task<IReadOnlyList<PurchaseOrderDto>> HandleAsync(GetCustomerOrdersQuery query, CancellationToken ct = default)
    {
        var lookup = autoMapper.Map<GetCustomerOrdersQuery, CustomerIdLookupDataModel>(query);
        return customerService.GetCustomerOrdersAsync(lookup.CustomerId, ct);
    }
}

public sealed class GetCustomerInvoicesQueryHandler(
    ICustomerService customerService,
    IMapper autoMapper) : IQueryHandler<GetCustomerInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>
{
    public Task<IReadOnlyList<B2BInvoiceDto>> HandleAsync(GetCustomerInvoicesQuery query, CancellationToken ct = default)
    {
        var lookup = autoMapper.Map<GetCustomerInvoicesQuery, CustomerIdLookupDataModel>(query);
        return customerService.GetCustomerInvoicesAsync(lookup.CustomerId, ct);
    }
}

public sealed class GetCustomerDeliveriesQueryHandler(
    ICustomerService customerService,
    IMapper autoMapper) : IQueryHandler<GetCustomerDeliveriesQuery, IReadOnlyList<B2BShipmentDto>>
{
    public Task<IReadOnlyList<B2BShipmentDto>> HandleAsync(GetCustomerDeliveriesQuery query, CancellationToken ct = default)
    {
        var lookup = autoMapper.Map<GetCustomerDeliveriesQuery, CustomerIdLookupDataModel>(query);
        return customerService.GetCustomerDeliveriesAsync(lookup.CustomerId, ct);
    }
}

public sealed class GetCustomerFavoriteProductsQueryHandler(
    ICustomerService customerService,
    IMapper autoMapper) : IQueryHandler<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>
{
    public Task<IReadOnlyList<CustomerFavoriteProductDto>> HandleAsync(
        GetCustomerFavoriteProductsQuery query,
        CancellationToken ct = default)
    {
        var lookup = autoMapper.Map<GetCustomerFavoriteProductsQuery, CustomerIdLookupDataModel>(query);
        return customerService.GetCustomerFavoriteProductsAsync(lookup.CustomerId, ct);
    }
}

public sealed class GetCustomerWishlistQueryHandler(
    ICustomerService customerService,
    IMapper autoMapper) : IQueryHandler<GetCustomerWishlistQuery, IReadOnlyList<CustomerWishlistItemDto>>
{
    public Task<IReadOnlyList<CustomerWishlistItemDto>> HandleAsync(
        GetCustomerWishlistQuery query,
        CancellationToken ct = default)
    {
        var lookup = autoMapper.Map<GetCustomerWishlistQuery, CustomerIdLookupDataModel>(query);
        return customerService.GetCustomerWishlistAsync(lookup.CustomerId, ct);
    }
}
