namespace Customer.Service.Handlers;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Customer.Service.DataModels;
using Customer.Service.Services;

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

public sealed class CreateCustomTagCommandHandler(
    ICustomerService customerService,
    IMapper autoMapper) : ICommandHandler<CreateCustomTagCommand, CustomTagDto>
{
    public Task<CustomTagDto> HandleAsync(CreateCustomTagCommand command, CancellationToken ct = default)
    {
        var tagModel = autoMapper.Map<CreateCustomTagCommand, CustomTagDataModel>(command);
        return customerService.CreateCustomTagAsync(tagModel, ct);
    }
}

public sealed class DeleteCustomTagCommandHandler(
    ICustomerService customerService) : ICommandHandler<DeleteCustomTagCommand, bool>
{
    public Task<bool> HandleAsync(DeleteCustomTagCommand command, CancellationToken ct = default) =>
        customerService.DeleteCustomTagAsync(command.TagId, ct);
}

public sealed class AssignTagToCustomerCommandHandler(
    ICustomerService customerService,
    IMapper autoMapper) : ICommandHandler<AssignTagToCustomerCommand, B2BCustomerDto>
{
    public Task<B2BCustomerDto> HandleAsync(AssignTagToCustomerCommand command, CancellationToken ct = default)
    {
        var assignmentModel = autoMapper.Map<AssignTagToCustomerCommand, CustomerTagAssignmentDataModel>(command);
        return customerService.AssignTagToCustomerAsync(assignmentModel, ct);
    }
}

public sealed class RemoveTagFromCustomerCommandHandler(
    ICustomerService customerService,
    IMapper autoMapper) : ICommandHandler<RemoveTagFromCustomerCommand, B2BCustomerDto>
{
    public Task<B2BCustomerDto> HandleAsync(RemoveTagFromCustomerCommand command, CancellationToken ct = default)
    {
        var assignmentModel = autoMapper.Map<RemoveTagFromCustomerCommand, CustomerTagAssignmentDataModel>(command);
        return customerService.RemoveTagFromCustomerAsync(assignmentModel, ct);
    }
}

public sealed class AddManualFavoriteProductCommandHandler(
    ICustomerService customerService) : ICommandHandler<AddManualFavoriteProductCommand, CustomerFavoriteProductDto>
{
    public Task<CustomerFavoriteProductDto> HandleAsync(AddManualFavoriteProductCommand command, CancellationToken ct = default) =>
        customerService.AddManualFavoriteProductAsync(command.CustomerId, command.ProductId, ct);
}

public sealed class RemoveManualFavoriteProductCommandHandler(
    ICustomerService customerService) : ICommandHandler<RemoveManualFavoriteProductCommand, bool>
{
    public Task<bool> HandleAsync(RemoveManualFavoriteProductCommand command, CancellationToken ct = default) =>
        customerService.RemoveManualFavoriteProductAsync(command.CustomerId, command.ProductId, ct);
}

public sealed class AddProductToWishlistCommandHandler(
    ICustomerService customerService) : ICommandHandler<AddProductToWishlistCommand, CustomerWishlistItemDto>
{
    public Task<CustomerWishlistItemDto> HandleAsync(AddProductToWishlistCommand command, CancellationToken ct = default) =>
        customerService.AddProductToWishlistAsync(command.CustomerId, command.ProductId, command.Note, command.Priority, ct);
}

public sealed class UpdateWishlistItemCommandHandler(
    ICustomerService customerService) : ICommandHandler<UpdateWishlistItemCommand, CustomerWishlistItemDto>
{
    public Task<CustomerWishlistItemDto> HandleAsync(UpdateWishlistItemCommand command, CancellationToken ct = default) =>
        customerService.UpdateWishlistItemAsync(command.CustomerId, command.ProductId, command.Note, command.Priority, ct);
}

public sealed class RemoveProductFromWishlistCommandHandler(
    ICustomerService customerService) : ICommandHandler<RemoveProductFromWishlistCommand, bool>
{
    public Task<bool> HandleAsync(RemoveProductFromWishlistCommand command, CancellationToken ct = default) =>
        customerService.RemoveProductFromWishlistAsync(command.CustomerId, command.ProductId, ct);
}
