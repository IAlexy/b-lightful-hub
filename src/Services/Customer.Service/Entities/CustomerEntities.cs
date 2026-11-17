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
