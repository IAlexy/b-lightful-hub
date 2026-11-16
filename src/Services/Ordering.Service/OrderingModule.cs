namespace Ordering.Service;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Service.Mapping;
using Ordering.Service.Persistence;

public sealed class OrderingSequenceGenerator
{
    private int _sequence = 1001;
    public string NextOrderNumber() => $"PO-2026-{Interlocked.Increment(ref _sequence)}";
}

public static class OrderingServiceRegistration
{
    public static IServiceCollection AddOrderingMicroservice(
        this IServiceCollection services,
        string? sqliteConnectionString = null)
    {
        services.AddSingleton<OrderingSequenceGenerator>();

        // Register AutoMapper with OrderingAutoMapperProfile
        services.AddAutoMapper(new OrderingAutoMapperProfile());

        // Register EF Core OrderingDbContext backed by SQLite relational engine (Customers, Orders, OrderLines)
        var connString = string.IsNullOrWhiteSpace(sqliteConnectionString)
            ? $"DataSource=ordering_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            : sqliteConnectionString;

        var keepAliveConnection = new SqliteConnection(connString);
        keepAliveConnection.Open();
        services.AddSingleton(keepAliveConnection);

        services.AddDbContext<OrderingDbContext>(options =>
            options.UseSqlite(keepAliveConnection),
            contextLifetime: ServiceLifetime.Transient,
            optionsLifetime: ServiceLifetime.Singleton);

        // Ensure relational tables (Customers, Orders, OrderLines) are created in SQLite
        using (var tempProvider = services.BuildServiceProvider())
        using (var db = tempProvider.GetRequiredService<OrderingDbContext>())
        {
            db.Database.EnsureCreated();
        }

        // Register CQRS Command & Query Handlers
        services.AddTransient<ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderDto>, CreatePurchaseOrderCommandHandler>();
        services.AddTransient<IQueryHandler<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>, GetPurchaseOrdersQueryHandler>();
        services.AddTransient<IQueryHandler<GetPurchaseOrderByIdQuery, PurchaseOrderDto?>, GetPurchaseOrderByIdQueryHandler>();

        return services;
    }
}
