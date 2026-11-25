namespace Customer.Service;

using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using Customer.Service.GraphQL;
using Customer.Service.Handlers;
using Customer.Service.Mapping;
using Customer.Service.Persistence;
using Customer.Service.Services;
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

        // Register EF Core CustomerDbContext backed by SQLite relational engine
        // (3 tables: B2BCustomers, CustomTags, CustomerTagAssignments)
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

        // Ensure the 3 relational tables (B2BCustomers, CustomTags, CustomerTagAssignments) are created in SQLite
        using (var tempProvider = services.BuildServiceProvider())
        using (var db = tempProvider.GetRequiredService<CustomerDbContext>())
        {
            db.Database.EnsureCreated();
        }

        // Register Customer Service, CQRS Command Handlers, Query Handlers & Customer GraphQL Subgraph Resolver
        services.AddTransient<ICustomerService, CustomerCrudService>();

        // Command Handlers (in Handlers/CustomerCommandHandlers.cs)
        services.AddTransient<ICommandHandler<CreateB2BCustomerCommand, B2BCustomerDto>, CreateB2BCustomerCommandHandler>();
        services.AddTransient<ICommandHandler<UpdateB2BCustomerCommand, B2BCustomerDto>, UpdateB2BCustomerCommandHandler>();
        services.AddTransient<ICommandHandler<DeleteB2BCustomerCommand, bool>, DeleteB2BCustomerCommandHandler>();
        services.AddTransient<ICommandHandler<CreateCustomTagCommand, CustomTagDto>, CreateCustomTagCommandHandler>();
        services.AddTransient<ICommandHandler<DeleteCustomTagCommand, bool>, DeleteCustomTagCommandHandler>();
        services.AddTransient<ICommandHandler<AssignTagToCustomerCommand, B2BCustomerDto>, AssignTagToCustomerCommandHandler>();
        services.AddTransient<ICommandHandler<RemoveTagFromCustomerCommand, B2BCustomerDto>, RemoveTagFromCustomerCommandHandler>();
        services.AddTransient<ICommandHandler<AddManualFavoriteProductCommand, CustomerFavoriteProductDto>, AddManualFavoriteProductCommandHandler>();
        services.AddTransient<ICommandHandler<RemoveManualFavoriteProductCommand, bool>, RemoveManualFavoriteProductCommandHandler>();
        services.AddTransient<ICommandHandler<AddProductToWishlistCommand, CustomerWishlistItemDto>, AddProductToWishlistCommandHandler>();
        services.AddTransient<ICommandHandler<UpdateWishlistItemCommand, CustomerWishlistItemDto>, UpdateWishlistItemCommandHandler>();
        services.AddTransient<ICommandHandler<RemoveProductFromWishlistCommand, bool>, RemoveProductFromWishlistCommandHandler>();

        // Query Handlers (in Handlers/CustomerQueryHandlers.cs)
        services.AddTransient<IQueryHandler<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>, GetB2BCustomersQueryHandler>();
        services.AddTransient<IQueryHandler<GetB2BCustomerByIdQuery, B2BCustomerDto?>, GetB2BCustomerByIdQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomTagsQuery, IReadOnlyList<CustomTagDto>>, GetCustomTagsQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomTagByIdQuery, CustomTagDto?>, GetCustomTagByIdQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomerOrdersQuery, IReadOnlyList<PurchaseOrderDto>>, GetCustomerOrdersQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomerInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>, GetCustomerInvoicesQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomerDeliveriesQuery, IReadOnlyList<B2BShipmentDto>>, GetCustomerDeliveriesQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>, GetCustomerFavoriteProductsQueryHandler>();
        services.AddTransient<IQueryHandler<GetCustomerWishlistQuery, IReadOnlyList<CustomerWishlistItemDto>>, GetCustomerWishlistQueryHandler>();

        services.AddTransient<CustomerGraphQLResolver>();

        return services;
    }
}
