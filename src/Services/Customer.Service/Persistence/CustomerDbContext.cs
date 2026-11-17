namespace Customer.Service.Persistence;

using Customer.Service.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core DbContext mapping the 3 relational tables of the Customer Microservice:
/// 1. "B2BCustomers" (CustomerProfileEntity)
/// 2. "CustomTags" (CustomTagEntity — independent from any customer)
/// 3. "CustomerTagAssignments" (CustomerTagAssignmentEntity — many-to-many association table)
/// </summary>
public class CustomerDbContext(DbContextOptions<CustomerDbContext> options) : DbContext(options)
{
    public DbSet<CustomerProfileEntity> Customers => Set<CustomerProfileEntity>();
    public DbSet<CustomTagEntity> CustomTags => Set<CustomTagEntity>();
    public DbSet<CustomerTagAssignmentEntity> CustomerTagAssignments => Set<CustomerTagAssignmentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. Table 1: "B2BCustomers"
        modelBuilder.Entity<CustomerProfileEntity>(entity =>
        {
            entity.ToTable("B2BCustomers");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).IsRequired().HasMaxLength(200);
            entity.Property(c => c.SirenSiret).IsRequired().HasMaxLength(30);
            entity.Property(c => c.InvoicingAddress).IsRequired().HasMaxLength(350);
            entity.Property(c => c.DeliveryAddress).IsRequired().HasMaxLength(350);
            entity.Property(c => c.ContactEmail).HasMaxLength(150);
            entity.Property(c => c.PhoneNumber).HasMaxLength(40);
            entity.HasIndex(c => c.SirenSiret).IsUnique().HasDatabaseName("IX_B2BCustomers_SirenSiret");
        });

        // 2. Table 2: "CustomTags" (independent custom tag definitions)
        modelBuilder.Entity<CustomTagEntity>(entity =>
        {
            entity.ToTable("CustomTags");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(80);
            entity.Property(t => t.Value).HasMaxLength(250);
            entity.Property(t => t.Color).IsRequired().HasMaxLength(20);
            entity.HasIndex(t => t.Name).IsUnique().HasDatabaseName("IX_CustomTags_Name");
        });

        // 3. Table 3: "CustomerTagAssignments" (association table between Customers and CustomTags)
        modelBuilder.Entity<CustomerTagAssignmentEntity>(entity =>
        {
            entity.ToTable("CustomerTagAssignments");
            entity.HasKey(a => new { a.CustomerId, a.TagId });

            entity.HasOne(a => a.Customer)
                  .WithMany(c => c.TagAssignments)
                  .HasForeignKey(a => a.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Tag)
                  .WithMany(t => t.CustomerAssignments)
                  .HasForeignKey(a => a.TagId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
