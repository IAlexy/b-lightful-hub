namespace Customer.Service.DataModels;

/// <summary>
/// Domain exception thrown when Customer CRUD invariants are violated
/// (e.g. customer not found, duplicate SIREN/SIRET, or missing required fields).
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
/// Domain DataModel representing a free custom tag attached to a B2B Customer.
/// </summary>
public class CustomTagDataModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string Color { get; set; } = "#7C3AED";
}

/// <summary>
/// Domain DataModel representing a B2B Customer with SIREN/SIRET, Invoicing & Delivery addresses
/// (including automatic synchronization when UseInvoicingAddressForDelivery is true), and free custom tags.
/// </summary>
public class CustomerAccountDataModel
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
    public List<CustomTagDataModel> Tags { get; set; } = [];

    /// <summary>
    /// Normalizes SIREN/SIRET, applies the "same invoicing and delivery address" rule when
    /// <see cref="UseInvoicingAddressForDelivery"/> is true, normalizes custom tags, and validates required fields.
    /// </summary>
    public void ApplyBusinessRulesAndValidate()
    {
        Name = Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new CustomerDomainException("INVALID_CUSTOMER_DATA", "Customer name is required.");
        }

        SirenSiret = NormalizeSirenSiret(SirenSiret);
        if (string.IsNullOrWhiteSpace(SirenSiret) || SirenSiret.Length < 9)
        {
            throw new CustomerDomainException(
                "INVALID_CUSTOMER_DATA",
                "A valid SIREN (9 digits) or SIRET (14 digits) identifier is required.");
        }

        InvoicingAddress = InvoicingAddress?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(InvoicingAddress))
        {
            throw new CustomerDomainException("INVALID_CUSTOMER_DATA", "Invoicing address is required.");
        }

        // Feature: if invoicing and delivery addresses are marked as the same, copy InvoicingAddress automatically
        if (UseInvoicingAddressForDelivery)
        {
            DeliveryAddress = InvoicingAddress;
        }
        else
        {
            DeliveryAddress = DeliveryAddress?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(DeliveryAddress))
            {
                throw new CustomerDomainException(
                    "INVALID_CUSTOMER_DATA",
                    "Delivery address is required when UseInvoicingAddressForDelivery is false.");
            }

            if (string.Equals(InvoicingAddress, DeliveryAddress, StringComparison.OrdinalIgnoreCase))
            {
                UseInvoicingAddressForDelivery = true;
            }
        }

        ContactEmail = string.IsNullOrWhiteSpace(ContactEmail)
            ? $"contact@{SirenSiret.ToLowerInvariant()}.b2b.fr"
            : ContactEmail.Trim();

        NormalizeTags();
    }

    public static string NormalizeSirenSiret(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private void NormalizeTags()
    {
        var deduplicated = new List<CustomTagDataModel>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in Tags)
        {
            var trimmedName = tag.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName) || !seenNames.Add(trimmedName))
            {
                continue;
            }

            deduplicated.Add(new CustomTagDataModel
            {
                Id = tag.Id == Guid.Empty ? Guid.NewGuid() : tag.Id,
                CustomerId = Id,
                Name = trimmedName,
                Value = string.IsNullOrWhiteSpace(tag.Value) ? null : tag.Value.Trim(),
                Color = string.IsNullOrWhiteSpace(tag.Color) ? "#7C3AED" : tag.Color.Trim()
            });
        }

        Tags = deduplicated;
    }
}
