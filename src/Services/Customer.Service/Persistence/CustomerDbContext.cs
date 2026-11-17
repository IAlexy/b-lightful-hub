namespace Customer.Service.Persistence;

using Customer.Service.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core DbContext mapping the Customer Microservice relational schema:
/// CustomerProfileEntity (1) ---> (*) CustomerCustomTagEntity.
/// </summary>
public class CustomerDbContext(DbContextOptions<CustomerDbContext> options) : DbContext(options)
{
    public DbSet<CustomerProfileEntity> Customers => Set<CustomerProfileEntity>();
    public DbSet<CustomerCustomTagEntity> CustomTags => Set<CustomerCustomTagEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. CustomerProfileEntity -> "B2BCustomers" table
        modelBuilder.Entity<CustomerProfileEntity>(entity =>
        {
            entity.ToTable("B2BCustomers");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).IsRequired().HasMaxLength(200);
            entity.Property(c => c.SirenSiret).IsRequired().HasMaxLength(30);
            entity.Property(c => c.InvoicingAddress).IsRequired().HasMaxLength(350);
            entity.Property(c => c.DeliveryAddress).IsRequired().HasMaxLength(350);
            entity.Property(c => c.ContactEmail).HasMaxLength(150);
            entity.HasIndex(c => c.SirenSiret).IsUnique().HasDatabaseName("IX_B2BCustomers_SirenSiret");
        });

        // 2. CustomerCustomTagEntity -> "CustomerCustomTags" table (1 Customer -> N Tags)
        modelBuilder.Entity<CustomerCustomTagEntity>(entity =>
        {
            entity.ToTable("CustomerCustomTags");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(80);
            entity.Property(t => t.Value).HasMaxLength(250);
            entity.Property(t => t.Color).IsRequired().HasMaxLength(20);

            entity.HasOne(t => t.Customer)
                  .WithMany(c => c.Tags)
                  .HasForeignKey(t => t.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
