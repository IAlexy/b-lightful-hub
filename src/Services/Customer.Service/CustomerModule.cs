namespace Customer.Service;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Customer.Service.GraphQL;
using Customer.Service.Mapping;
using Customer.Service.Persistence;
using Delivery.Service;
using Invoicing.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Service;

public static class CustomerServiceRegistration
{
    public static IServiceCollection AddCustomerMicroservice(
        this IServiceCollection services,
        string? sqliteConnectionString = null)
    {
        // Register AutoMapper with CustomerAutoMapperProfile
        services.AddAutoMapper(new CustomerAutoMapperProfile());

        // Register EF Core CustomerDbContext backed by SQLite relational engine (B2BCustomers & CustomerCustomTags)
        var connString = string.IsNullOrWhiteSpace(sqliteConnectionString)
            ? $"DataSource=customer_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            : sqliteConnectionString;

        var keepAliveConnection = new SqliteConnection(connString);
        keepAliveConnection.Open();
        services.AddSingleton(keepAliveConnection);

        services.AddDbContext<CustomerDbContext>(options =>
            options.UseSqlite(keepAliveConnection),
            contextLifetime: ServiceLifetime.Transient,
            optionsLifetime: ServiceLifetime.Singleton);

        // Ensure relational tables (B2BCustomers, CustomerCustomTags) are created in SQLite
        using (var tempProvider = services.BuildServiceProvider())
        using (var db = tempProvider.GetRequiredService<CustomerDbContext>())
        {
            db.Database.EnsureCreated();
        }

        // Register Customer Service, CQRS Command/Query Handlers & Customer GraphQL Subgraph Resolver
        services.AddTransient<ICustomerService, CustomerCrudService>();
        services.AddTransient<ICommandHandler<CreateB2BCustomerCommand, B2BCustomerDto>, CreateB2BCustomerCommandHandler>();
        services.AddTransient<ICommandHandler<UpdateB2BCustomerCommand, B2BCustomerDto>, UpdateB2BCustomerCommandHandler>();
        services.AddTransient<ICommandHandler<DeleteB2BCustomerCommand, bool>, DeleteB2BCustomerCommandHandler>();
        services.AddTransient<IQueryHandler<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>, GetB2BCustomersQueryHandler>();
        services.AddTransient<IQueryHandler<GetB2BCustomerByIdQuery, B2BCustomerDto?>, GetB2BCustomerByIdQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomerOrdersQuery, IReadOnlyList<PurchaseOrderDto>>, GetCustomerOrdersQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomerInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>, GetCustomerInvoicesQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomerDeliveriesQuery, IReadOnlyList<B2BShipmentDto>>, GetCustomerDeliveriesQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>, GetCustomerFavoriteProductsQueryHandler>();
        services.AddTransient<CustomerGraphQLResolver>();

        return services;
    }
}
