namespace Customer.Service.Entities;

/// <summary>
/// Table 1 ("B2BCustomers"): SQL Database Entity representing a B2B Customer.
/// </summary>
public class CustomerProfileEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string SirenSiret { get; set; } = string.Empty;
    public string InvoicingAddress { get; set; } = string.Empty;
    public string DeliveryAddress { get; set; } = string.Empty;
    public bool UseInvoicingAddressForDelivery { get; set; }
    public string? ContactEmail { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation property to the 3rd association table ("CustomerTagAssignments")
    public ICollection<CustomerTagAssignmentEntity> TagAssignments { get; set; } = new List<CustomerTagAssignmentEntity>();

    // Navigation properties for Manual Favorite Products & Wishlist
    public ICollection<CustomerManualFavoriteEntity> ManualFavorites { get; set; } = new List<CustomerManualFavoriteEntity>();
    public ICollection<CustomerWishlistItemEntity> WishlistItems { get; set; } = new List<CustomerWishlistItemEntity>();
}

/// <summary>
/// Table 2 ("CustomTags"): SQL Database Entity representing an independent Custom Tag
/// that can be created on its own and attached to any number of B2B Customers.
/// </summary>
public class CustomTagEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string Color { get; set; } = "#7C3AED";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation property to the 3rd association table ("CustomerTagAssignments")
    public ICollection<CustomerTagAssignmentEntity> CustomerAssignments { get; set; } = new List<CustomerTagAssignmentEntity>();
}

/// <summary>
/// Table 3 ("CustomerTagAssignments"): Many-to-Many Association Entity linking
/// B2BCustomers (CustomerProfileEntity) and independent CustomTags (CustomTagEntity).
/// </summary>
public class CustomerTagAssignmentEntity
{
    public Guid CustomerId { get; set; }
    public CustomerProfileEntity Customer { get; set; } = null!;

    public Guid TagId { get; set; }
    public CustomTagEntity Tag { get; set; } = null!;

    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Table 4 ("CustomerManualFavorites"): Products manually pinned as frequently reordered favorites by a B2B Customer.
/// Combined with automatic purchase frequency in GetCustomerFavoriteProducts.
/// </summary>
public class CustomerManualFavoriteEntity
{
    public Guid CustomerId { get; set; }
    public CustomerProfileEntity Customer { get; set; } = null!;

    public Guid ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Table 5 ("CustomerWishlistItems"): Curated personal wishlist of products of the customer's taste,
/// separate from frequently ordered favorite products.
/// </summary>
public class CustomerWishlistItemEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }
    public CustomerProfileEntity Customer { get; set; } = null!;

    public Guid ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int Priority { get; set; } = 3;
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}
