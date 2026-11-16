namespace B2B.BuildingBlocks.Cqrs;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Marker interface for any CQRS request (Command or Query) returning <typeparamref name="TResponse"/>.
/// </summary>
public interface IRequest<out TResponse>;

/// <summary>
/// Represents a state-mutating Write Command in the CQRS pattern.
/// </summary>
public interface ICommand<out TResponse> : IRequest<TResponse>;

/// <summary>
/// Represents a side-effect-free Read Query in the CQRS pattern.
/// </summary>
public interface IQuery<out TResponse> : IRequest<TResponse>;

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<TResponse> HandleAsync(TCommand command, CancellationToken ct = default);
}

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<TResponse> HandleAsync(TQuery query, CancellationToken ct = default);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>
/// Cross-cutting pipeline behavior (validation, audit logging, performance metrics) wrapping CQRS handlers.
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct = default);
}

public interface ISender
{
    Task<TResponse> SendCommandAsync<TCommand, TResponse>(TCommand command, CancellationToken ct = default)
        where TCommand : ICommand<TResponse>;

    Task<TResponse> SendQueryAsync<TQuery, TResponse>(TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResponse>;
}

public sealed class CqrsDispatcher(IServiceProvider serviceProvider) : ISender
{
    public Task<TResponse> SendCommandAsync<TCommand, TResponse>(TCommand command, CancellationToken ct = default)
        where TCommand : ICommand<TResponse>
    {
        ArgumentNullException.ThrowIfNull(command);
        var handler = serviceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TCommand, TResponse>>().Reverse().ToList();

        RequestHandlerDelegate<TResponse> pipeline = () => handler.HandleAsync(command, ct);
        foreach (var behavior in behaviors)
        {
            var next = pipeline;
            pipeline = () => behavior.HandleAsync(command, next, ct);
        }

        return pipeline();
    }

    public Task<TResponse> SendQueryAsync<TQuery, TResponse>(TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResponse>
    {
        ArgumentNullException.ThrowIfNull(query);
        var handler = serviceProvider.GetRequiredService<IQueryHandler<TQuery, TResponse>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TQuery, TResponse>>().Reverse().ToList();

        RequestHandlerDelegate<TResponse> pipeline = () => handler.HandleAsync(query, ct);
        foreach (var behavior in behaviors)
        {
            var next = pipeline;
            pipeline = () => behavior.HandleAsync(query, next, ct);
        }

        return pipeline();
    }
}
