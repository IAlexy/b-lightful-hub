namespace Customer.Service.Entities;

/// <summary>
/// SQL Database Entity representing a B2B Customer in the Customer Microservice.
/// Mapped to the "B2BCustomers" table via EF Core.
/// </summary>
public class CustomerProfileEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string SirenSiret { get; set; } = string.Empty;
    public string InvoicingAddress { get; set; } = string.Empty;
    public string DeliveryAddress { get; set; } = string.Empty;
    public bool UseInvoicingAddressForDelivery { get; set; }
    public string ContactEmail { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation property: 1 Customer -> N Custom Free Tags
    public ICollection<CustomerCustomTagEntity> Tags { get; set; } = new List<CustomerCustomTagEntity>();
}

/// <summary>
/// SQL Database Entity representing a Free Custom Tag attached to a B2B Customer.
/// Mapped to the "CustomerCustomTags" table via EF Core.
/// </summary>
public class CustomerCustomTagEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }
    public CustomerProfileEntity Customer { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string Color { get; set; } = "#7C3AED";
}
