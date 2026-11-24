namespace Catalog.Service.Persistence;

using Catalog.Service.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core DbContext mapping the 6 relational tables of the Catalog Microservice:
/// 1. "CatalogCategories" (CatalogCategoryEntity)
/// 2. "CatalogProducts" (CatalogProductEntity)
/// 3. "ProductTags" (ProductTagEntity — independent from any product)
/// 4. "ProductTagAssignments" (ProductTagAssignmentEntity — many-to-many join table)
/// 5. "ProductVolumePriceTiers" (ProductVolumePriceTierEntity — multi-tier B2B pricing brackets)
/// 6. "StockRefillAlerts" (StockRefillAlertEntity — back-in-stock email subscriptions)
/// </summary>
public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<CatalogCategoryEntity> Categories => Set<CatalogCategoryEntity>();
    public DbSet<CatalogProductEntity> Products => Set<CatalogProductEntity>();
    public DbSet<ProductTagEntity> ProductTags => Set<ProductTagEntity>();
    public DbSet<ProductTagAssignmentEntity> ProductTagAssignments => Set<ProductTagAssignmentEntity>();
    public DbSet<ProductVolumePriceTierEntity> VolumePriceTiers => Set<ProductVolumePriceTierEntity>();
    public DbSet<StockRefillAlertEntity> StockRefillAlerts => Set<StockRefillAlertEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. Table 1: "CatalogCategories"
        modelBuilder.Entity<CatalogCategoryEntity>(entity =>
        {
            entity.ToTable("CatalogCategories");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).IsRequired().HasMaxLength(120);
            entity.Property(c => c.Slug).IsRequired().HasMaxLength(140);
            entity.Property(c => c.Description).HasMaxLength(500);
            entity.HasIndex(c => c.Name).IsUnique().HasDatabaseName("IX_CatalogCategories_Name");
            entity.HasIndex(c => c.Slug).IsUnique().HasDatabaseName("IX_CatalogCategories_Slug");
        });

        // 2. Table 2: "CatalogProducts"
        modelBuilder.Entity<CatalogProductEntity>(entity =>
        {
            entity.ToTable("CatalogProducts");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Sku).IsRequired().HasMaxLength(64);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(200);
            entity.Property(p => p.Description).HasMaxLength(1000);
            entity.Property(p => p.UnitPriceHt).HasPrecision(18, 2);
            entity.Property(p => p.BulkUnitPriceHt).HasPrecision(18, 2);
            entity.HasIndex(p => p.Sku).IsUnique().HasDatabaseName("IX_CatalogProducts_Sku");

            entity.HasOne(p => p.Category)
                  .WithMany(c => c.Products)
                  .HasForeignKey(p => p.CategoryId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // 3. Table 3: "ProductTags"
        modelBuilder.Entity<ProductTagEntity>(entity =>
        {
            entity.ToTable("ProductTags");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(80);
            entity.Property(t => t.Value).HasMaxLength(250);
            entity.Property(t => t.Color).IsRequired().HasMaxLength(20);
            entity.HasIndex(t => t.Name).IsUnique().HasDatabaseName("IX_ProductTags_Name");
        });

        // 4. Table 4: "ProductTagAssignments"
        modelBuilder.Entity<ProductTagAssignmentEntity>(entity =>
        {
            entity.ToTable("ProductTagAssignments");
            entity.HasKey(a => new { a.ProductId, a.TagId });

            entity.HasOne(a => a.Product)
                  .WithMany(p => p.TagAssignments)
                  .HasForeignKey(a => a.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Tag)
                  .WithMany(t => t.ProductAssignments)
                  .HasForeignKey(a => a.TagId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // 5. Table 5: "ProductVolumePriceTiers"
        modelBuilder.Entity<ProductVolumePriceTierEntity>(entity =>
        {
            entity.ToTable("ProductVolumePriceTiers");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.UnitPriceHt).HasPrecision(18, 2);
            entity.HasIndex(t => new { t.ProductId, t.MinQuantity })
                  .IsUnique()
                  .HasDatabaseName("IX_ProductVolumePriceTiers_ProductId_MinQuantity");

            entity.HasOne(t => t.Product)
                  .WithMany(p => p.VolumePriceTiers)
                  .HasForeignKey(t => t.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // 6. Table 6: "StockRefillAlerts"
        modelBuilder.Entity<StockRefillAlertEntity>(entity =>
        {
            entity.ToTable("StockRefillAlerts");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Email).IsRequired().HasMaxLength(200);
            entity.HasIndex(a => new { a.ProductId, a.Email, a.IsNotified })
                  .HasDatabaseName("IX_StockRefillAlerts_Product_Email_Notified");

            entity.HasOne(a => a.Product)
                  .WithMany(p => p.StockRefillAlerts)
                  .HasForeignKey(a => a.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
