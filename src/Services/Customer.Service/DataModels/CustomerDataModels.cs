namespace Customer.Service.DataModels;

/// <summary>
/// Domain exception thrown when Customer or CustomTag business rules are violated.
/// </summary>
public sealed class CustomerDomainException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Exception thrown when requesting a customer's favorite products before the Catalog & Products
/// persistence feature is implemented.
/// </summary>
public sealed class CustomerFavoriteProductsNotImplementedException(Guid customerId, string customerName)
    : NotImplementedException(
        $"Retrieving favorite products for customer '{customerName}' ({customerId}) is not implemented yet: the full Catalog & Products feature will be implemented in a future iteration.")
{
    public string ErrorCode { get; } = "CATALOG_FAVORITE_PRODUCTS_NOT_IMPLEMENTED";
    public Guid CustomerId { get; } = customerId;
}

/// <summary>
/// Pure DataModel representing an independent Custom Tag.
/// Contains no business logic; treated by the CustomerCrudService.
/// </summary>
public class CustomTagDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string Color { get; set; } = "#7C3AED";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Pure DataModel representing a Customer <-> CustomTag association.
/// Contains no business logic; treated by the CustomerCrudService.
/// </summary>
public class CustomerTagAssignmentDataModel
{
    public Guid CustomerId { get; set; }
    public Guid TagId { get; set; }
}

/// <summary>
/// Pure DataModel representing a B2B Customer.
/// Contains no business logic; all validation and address/contact rules are executed in CustomerCrudService.
/// </summary>
public class CustomerAccountDataModel
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
    public List<Guid> TagIds { get; set; } = [];
    public List<CustomTagDataModel> Tags { get; set; } = [];
}

/// <summary>
/// Pure DataModel for filtering customers in list queries.
/// </summary>
public class CustomerFilterDataModel
{
    public string? Search { get; set; }
    public string? Tag { get; set; }
}

/// <summary>
/// Pure DataModel for looking up a resource by CustomerId.
/// </summary>
public class CustomerIdLookupDataModel
{
    public Guid CustomerId { get; set; }
}
