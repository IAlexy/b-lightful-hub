namespace B2B.BuildingBlocks.Notifications;

using System.Collections.Concurrent;

public sealed record EmailMessage(
    Guid Id,
    string ToEmail,
    string Subject,
    string Body,
    string Category,
    DateTime SentAtUtc,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Shared email notification abstraction for B2B microservices
/// (used by Catalog.Service for back-in-stock refill alerts, and reusable by Ordering.Service for order confirmations).
/// </summary>
public interface IEmailNotificationService
{
    Task<EmailMessage> SendEmailAsync(
        string toEmail,
        string subject,
        string body,
        string category,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    IReadOnlyList<EmailMessage> SentEmails { get; }
}

public sealed class InMemoryEmailNotificationService : IEmailNotificationService
{
    private readonly ConcurrentQueue<EmailMessage> _sentEmails = new();

    public IReadOnlyList<EmailMessage> SentEmails => _sentEmails.ToArray();

    public Task<EmailMessage> SendEmailAsync(
        string toEmail,
        string subject,
        string body,
        string category,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        var message = new EmailMessage(
            Id: Guid.NewGuid(),
            ToEmail: toEmail.Trim(),
            Subject: subject.Trim(),
            Body: body,
            Category: category.Trim(),
            SentAtUtc: DateTime.UtcNow,
            Metadata: metadata);

        _sentEmails.Enqueue(message);
        return Task.FromResult(message);
    }
}
