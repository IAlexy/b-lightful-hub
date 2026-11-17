namespace Ordering.Service;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Microsoft.EntityFrameworkCore;
using Ordering.Service.DataModels;
using Ordering.Service.Entities;
using Ordering.Service.Persistence;

public sealed class GetPurchaseOrdersQueryHandler(
    OrderingDbContext dbContext,
    IMapper autoMapper) : IQueryHandler<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>
{
    public async Task<IReadOnlyList<PurchaseOrderDto>> HandleAsync(GetPurchaseOrdersQuery query, CancellationToken ct = default)
    {
        IQueryable<OrderEntity> sqlQuery = dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.Lines);

        if (!string.IsNullOrWhiteSpace(query.BuyerVatNumber))
        {
            sqlQuery = sqlQuery.Where(o => o.Customer.VatNumber == query.BuyerVatNumber);
        }

        var entities = await sqlQuery
            .OrderByDescending(o => o.CreatedAtUtc)
            .ToListAsync(ct);

        // Map SQL Entities -> Domain DataModels -> Output DTOs via AutoMapper
        var dataModels = autoMapper.Map<List<OrderDataModel>>(entities);
        return autoMapper.Map<List<PurchaseOrderDto>>(dataModels);
    }
}

public sealed class GetPurchaseOrderByIdQueryHandler(
    OrderingDbContext dbContext,
    IMapper autoMapper) : IQueryHandler<GetPurchaseOrderByIdQuery, PurchaseOrderDto?>
{
    public async Task<PurchaseOrderDto?> HandleAsync(GetPurchaseOrderByIdQuery query, CancellationToken ct = default)
    {
        var entity = await dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == query.OrderId, ct);

        if (entity is null)
        {
            return null;
        }

        // Map SQL Entity -> Domain DataModel -> Output DTO via AutoMapper
        var dataModel = autoMapper.Map<OrderEntity, OrderDataModel>(entity);
        return autoMapper.Map<OrderDataModel, PurchaseOrderDto>(dataModel);
    }
}
