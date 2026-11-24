namespace Catalog.Service.Handlers;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Catalog.Service.DataModels;
using Catalog.Service.Services;

public sealed class CreateCatalogProductCommandHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : ICommandHandler<CreateCatalogProductCommand, CatalogProductDto>
{
    public Task<CatalogProductDto> HandleAsync(CreateCatalogProductCommand command, CancellationToken ct = default)
    {
        var model = autoMapper.Map<CreateCatalogProductCommand, CatalogProductDataModel>(command);
        return catalogService.CreateProductAsync(model, ct);
    }
}

public sealed class UpdateCatalogProductCommandHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : ICommandHandler<UpdateCatalogProductCommand, CatalogProductDto>
{
    public Task<CatalogProductDto> HandleAsync(UpdateCatalogProductCommand command, CancellationToken ct = default)
    {
        var model = autoMapper.Map<UpdateCatalogProductCommand, CatalogProductDataModel>(command);
        return catalogService.UpdateProductAsync(model, ct);
    }
}

public sealed class ArchiveCatalogProductCommandHandler(
    ICatalogService catalogService) : ICommandHandler<ArchiveCatalogProductCommand, CatalogProductDto>
{
    public Task<CatalogProductDto> HandleAsync(ArchiveCatalogProductCommand command, CancellationToken ct = default) =>
        catalogService.ArchiveProductAsync(command.ProductId, ct);
}

public sealed class UnarchiveCatalogProductCommandHandler(
    ICatalogService catalogService) : ICommandHandler<UnarchiveCatalogProductCommand, CatalogProductDto>
{
    public Task<CatalogProductDto> HandleAsync(UnarchiveCatalogProductCommand command, CancellationToken ct = default) =>
        catalogService.UnarchiveProductAsync(command.ProductId, ct);
}

public sealed class CreateCatalogCategoryCommandHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : ICommandHandler<CreateCatalogCategoryCommand, CatalogCategoryDto>
{
    public Task<CatalogCategoryDto> HandleAsync(CreateCatalogCategoryCommand command, CancellationToken ct = default)
    {
        var model = autoMapper.Map<CreateCatalogCategoryCommand, CatalogCategoryDataModel>(command);
        return catalogService.CreateCategoryAsync(model, ct);
    }
}

public sealed class UpdateCatalogCategoryCommandHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : ICommandHandler<UpdateCatalogCategoryCommand, CatalogCategoryDto>
{
    public Task<CatalogCategoryDto> HandleAsync(UpdateCatalogCategoryCommand command, CancellationToken ct = default)
    {
        var model = autoMapper.Map<UpdateCatalogCategoryCommand, CatalogCategoryDataModel>(command);
        return catalogService.UpdateCategoryAsync(model, ct);
    }
}

public sealed class DeleteCatalogCategoryCommandHandler(
    ICatalogService catalogService) : ICommandHandler<DeleteCatalogCategoryCommand, bool>
{
    public Task<bool> HandleAsync(DeleteCatalogCategoryCommand command, CancellationToken ct = default) =>
        catalogService.DeleteCategoryAsync(command.CategoryId, ct);
}

public sealed class CreateProductTagCommandHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : ICommandHandler<CreateProductTagCommand, ProductTagDto>
{
    public Task<ProductTagDto> HandleAsync(CreateProductTagCommand command, CancellationToken ct = default)
    {
        var model = autoMapper.Map<CreateProductTagCommand, ProductTagDataModel>(command);
        return catalogService.CreateProductTagAsync(model, ct);
    }
}

public sealed class DeleteProductTagCommandHandler(
    ICatalogService catalogService) : ICommandHandler<DeleteProductTagCommand, bool>
{
    public Task<bool> HandleAsync(DeleteProductTagCommand command, CancellationToken ct = default) =>
        catalogService.DeleteProductTagAsync(command.TagId, ct);
}

public sealed class AssignTagToProductCommandHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : ICommandHandler<AssignTagToProductCommand, CatalogProductDto>
{
    public Task<CatalogProductDto> HandleAsync(AssignTagToProductCommand command, CancellationToken ct = default)
    {
        var assignment = autoMapper.Map<AssignTagToProductCommand, ProductTagAssignmentDataModel>(command);
        return catalogService.AssignTagToProductAsync(assignment.ProductId, assignment.TagId, ct);
    }
}

public sealed class RemoveTagFromProductCommandHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : ICommandHandler<RemoveTagFromProductCommand, CatalogProductDto>
{
    public Task<CatalogProductDto> HandleAsync(RemoveTagFromProductCommand command, CancellationToken ct = default)
    {
        var assignment = autoMapper.Map<RemoveTagFromProductCommand, ProductTagAssignmentDataModel>(command);
        return catalogService.RemoveTagFromProductAsync(assignment.ProductId, assignment.TagId, ct);
    }
}

public sealed class SetProductVolumePriceTiersCommandHandler(
    ICatalogService catalogService,
    IMapper autoMapper) : ICommandHandler<SetProductVolumePriceTiersCommand, CatalogProductDto>
{
    public Task<CatalogProductDto> HandleAsync(SetProductVolumePriceTiersCommand command, CancellationToken ct = default)
    {
        var tierModels = command.Tiers
            .Select(t => autoMapper.Map<VolumePriceTierInput, VolumePriceTierDataModel>(t))
            .ToList();
        return catalogService.SetVolumePriceTiersAsync(command.ProductId, tierModels, ct);
    }
}

public sealed class ReserveCatalogStockCommandHandler(
    ICatalogService catalogService) : ICommandHandler<ReserveCatalogStockCommand, CatalogProductDto?>
{
    public Task<CatalogProductDto?> HandleAsync(ReserveCatalogStockCommand command, CancellationToken ct = default) =>
        catalogService.ReserveStockAsync(command.Sku, command.Quantity, ct);
}

public sealed class ReleaseCatalogStockCommandHandler(
    ICatalogService catalogService) : ICommandHandler<ReleaseCatalogStockCommand, CatalogProductDto?>
{
    public Task<CatalogProductDto?> HandleAsync(ReleaseCatalogStockCommand command, CancellationToken ct = default) =>
        catalogService.ReleaseStockAsync(command.Sku, command.Quantity, ct);
}

public sealed class RestockCatalogProductCommandHandler(
    ICatalogService catalogService) : ICommandHandler<RestockCatalogProductCommand, RestockResultDto>
{
    public Task<RestockResultDto> HandleAsync(RestockCatalogProductCommand command, CancellationToken ct = default) =>
        catalogService.RestockProductAsync(command.ProductId, command.QuantityAdded, ct);
}

public sealed class AdjustCatalogStockCommandHandler(
    ICatalogService catalogService) : ICommandHandler<AdjustCatalogStockCommand, RestockResultDto>
{
    public Task<RestockResultDto> HandleAsync(AdjustCatalogStockCommand command, CancellationToken ct = default) =>
        catalogService.AdjustStockAsync(command.ProductId, command.NewAvailableStock, command.Reason, ct);
}

public sealed class SubscribeStockRefillAlertCommandHandler(
    ICatalogService catalogService) : ICommandHandler<SubscribeStockRefillAlertCommand, StockRefillAlertDto>
{
    public Task<StockRefillAlertDto> HandleAsync(SubscribeStockRefillAlertCommand command, CancellationToken ct = default) =>
        catalogService.SubscribeStockRefillAlertAsync(command.ProductId, command.Email, command.CustomerId, ct);
}

public sealed class UnsubscribeStockRefillAlertCommandHandler(
    ICatalogService catalogService) : ICommandHandler<UnsubscribeStockRefillAlertCommand, bool>
{
    public Task<bool> HandleAsync(UnsubscribeStockRefillAlertCommand command, CancellationToken ct = default) =>
        catalogService.UnsubscribeStockRefillAlertAsync(command.AlertId, ct);
}
