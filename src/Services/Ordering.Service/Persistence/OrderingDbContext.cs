namespace Ordering.Service.Persistence;

using Microsoft.EntityFrameworkCore;
using Ordering.Service.Entities;

/// <summary>
/// EF Core DbContext mapping the relational B2B Ordering schema:
/// CustomerEntity (1) ---> (*) OrderEntity (1) ---> (*) OrderLineEntity.
/// </summary>
public class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderLineEntity> OrderLines => Set<OrderLineEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. CustomerEntity -> "Customers" table
        modelBuilder.Entity<CustomerEntity>(entity =>
        {
            entity.ToTable("Customers");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.CompanyName).IsRequired().HasMaxLength(200);
            entity.Property(c => c.VatNumber).IsRequired().HasMaxLength(50);
            entity.Property(c => c.ContactEmail).HasMaxLength(150);
            entity.Property(c => c.BillingAddress).HasMaxLength(350);
            entity.HasIndex(c => c.VatNumber).HasDatabaseName("IX_Customers_VatNumber");
        });

        // 2. OrderEntity -> "Orders" table (1 Customer -> N Orders)
        modelBuilder.Entity<OrderEntity>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.OrderNumber).IsRequired().HasMaxLength(50);
            entity.Property(o => o.ShippingDestination).IsRequired().HasMaxLength(350);
            entity.Property(o => o.Status).IsRequired().HasMaxLength(40);
            entity.Property(o => o.TotalAmountHt).HasColumnType("decimal(18,2)");
            entity.HasIndex(o => o.OrderNumber).IsUnique().HasDatabaseName("IX_Orders_OrderNumber");

            entity.HasOne(o => o.Customer)
                  .WithMany(c => c.Orders)
                  .HasForeignKey(o => o.CustomerId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // 3. OrderLineEntity -> "OrderLines" table (1 Order -> N OrderLines)
        modelBuilder.Entity<OrderLineEntity>(entity =>
        {
            entity.ToTable("OrderLines");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Sku).IsRequired().HasMaxLength(80);
            entity.Property(l => l.ProductName).IsRequired().HasMaxLength(250);
            entity.Property(l => l.UnitPriceHt).HasColumnType("decimal(18,2)");
            entity.Property(l => l.LineTotalHt).HasColumnType("decimal(18,2)");

            entity.HasOne(l => l.Order)
                  .WithMany(o => o.Lines)
                  .HasForeignKey(l => l.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
