namespace Invoicing.Service;

using System.Collections.Concurrent;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using Microsoft.Extensions.DependencyInjection;

public enum B2BInvoiceStatus
{
    Issued,
    Paid,
    Overdue
}

public sealed class B2BInvoice
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string InvoiceNumber { get; init; }
    public required Guid OrderId { get; init; }
    public required string OrderNumber { get; init; }
    public required string BuyerCompanyName { get; init; }
    public required string BuyerVatNumber { get; init; }
    public decimal SubtotalHt { get; init; }
    public decimal VatRatePercent { get; init; } = 20.0m;
    public decimal VatAmount => Math.Round(SubtotalHt * (VatRatePercent / 100m), 2);
    public decimal TotalTtc => SubtotalHt + VatAmount;
    public B2BInvoiceStatus Status { get; set; } = B2BInvoiceStatus.Issued;
    public DateTime IssuedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime DueDateUtc { get; init; }
    public DateTime? PaidAtUtc { get; set; }
}

public sealed record B2BInvoiceDto(
    Guid Id,
    string InvoiceNumber,
    Guid OrderId,
    string OrderNumber,
    string BuyerCompanyName,
    string BuyerVatNumber,
    decimal SubtotalHt,
    decimal VatRatePercent,
    decimal VatAmount,
    decimal TotalTtc,
    string Status,
    DateTime IssuedAtUtc,
    DateTime DueDateUtc,
    DateTime? PaidAtUtc);

public sealed class InvoiceToDtoProfile : IMapProfile<B2BInvoice, B2BInvoiceDto>
{
    public B2BInvoiceDto Map(B2BInvoice source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new B2BInvoiceDto(
            source.Id,
            source.InvoiceNumber,
            source.OrderId,
            source.OrderNumber,
            source.BuyerCompanyName,
            source.BuyerVatNumber,
            source.SubtotalHt,
            source.VatRatePercent,
            source.VatAmount,
            source.TotalTtc,
            source.Status.ToString(),
            source.IssuedAtUtc,
            source.DueDateUtc,
            source.PaidAtUtc);
    }
}

public sealed class InvoicingDataStore
{
    private int _seq = 5001;
    public ConcurrentDictionary<Guid, B2BInvoice> Invoices { get; } = new();

    public string NextInvoiceNumber() => $"INV-2026-{Interlocked.Increment(ref _seq)}";
}

public sealed record IssueInvoiceCommand(
    Guid OrderId,
    string OrderNumber,
    string BuyerCompanyName,
    string BuyerVatNumber,
    decimal SubtotalHt,
    int PaymentTermsDays = 30) : ICommand<B2BInvoiceDto>;

public sealed class IssueInvoiceCommandHandler(
    InvoicingDataStore store,
    IB2BMapper mapper) : ICommandHandler<IssueInvoiceCommand, B2BInvoiceDto>
{
    public Task<B2BInvoiceDto> HandleAsync(IssueInvoiceCommand command, CancellationToken ct = default)
    {
        var existing = store.Invoices.Values.FirstOrDefault(i => i.OrderId == command.OrderId);
        if (existing is not null)
        {
            return Task.FromResult(mapper.Map<B2BInvoice, B2BInvoiceDto>(existing));
        }

        var invoice = new B2BInvoice
        {
            InvoiceNumber = store.NextInvoiceNumber(),
            OrderId = command.OrderId,
            OrderNumber = command.OrderNumber,
            BuyerCompanyName = command.BuyerCompanyName,
            BuyerVatNumber = command.BuyerVatNumber,
            SubtotalHt = command.SubtotalHt,
            DueDateUtc = DateTime.UtcNow.AddDays(Math.Max(15, command.PaymentTermsDays))
        };

        store.Invoices[invoice.Id] = invoice;
        return Task.FromResult(mapper.Map<B2BInvoice, B2BInvoiceDto>(invoice));
    }
}

public sealed record MarkInvoicePaidCommand(Guid InvoiceId) : ICommand<B2BInvoiceDto?>;

public sealed class MarkInvoicePaidCommandHandler(
    InvoicingDataStore store,
    IB2BMapper mapper) : ICommandHandler<MarkInvoicePaidCommand, B2BInvoiceDto?>
{
    public Task<B2BInvoiceDto?> HandleAsync(MarkInvoicePaidCommand command, CancellationToken ct = default)
    {
        if (!store.Invoices.TryGetValue(command.InvoiceId, out var invoice))
        {
            return Task.FromResult<B2BInvoiceDto?>(null);
        }

        invoice.Status = B2BInvoiceStatus.Paid;
        invoice.PaidAtUtc = DateTime.UtcNow;
        return Task.FromResult<B2BInvoiceDto?>(mapper.Map<B2BInvoice, B2BInvoiceDto>(invoice));
    }
}

public sealed record GetInvoicesQuery : IQuery<IReadOnlyList<B2BInvoiceDto>>;

public sealed class GetInvoicesQueryHandler(
    InvoicingDataStore store,
    IB2BMapper mapper) : IQueryHandler<GetInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>
{
    public Task<IReadOnlyList<B2BInvoiceDto>> HandleAsync(GetInvoicesQuery query, CancellationToken ct = default)
    {
        var list = store.Invoices.Values.OrderByDescending(i => i.IssuedAtUtc).ToList();
        return Task.FromResult(mapper.MapList<B2BInvoice, B2BInvoiceDto>(list));
    }
}

public sealed record GetInvoiceByOrderIdQuery(Guid OrderId) : IQuery<B2BInvoiceDto?>;

public sealed class GetInvoiceByOrderIdQueryHandler(
    InvoicingDataStore store,
    IB2BMapper mapper) : IQueryHandler<GetInvoiceByOrderIdQuery, B2BInvoiceDto?>
{
    public Task<B2BInvoiceDto?> HandleAsync(GetInvoiceByOrderIdQuery query, CancellationToken ct = default)
    {
        var invoice = store.Invoices.Values.FirstOrDefault(i => i.OrderId == query.OrderId);
        return Task.FromResult(invoice is null ? null : mapper.Map<B2BInvoice, B2BInvoiceDto>(invoice));
    }
}

public sealed class OrderConfirmedInvoiceHandler(ISender sender)
    : IIntegrationEventHandler<OrderConfirmedIntegrationEvent>
{
    public async Task HandleAsync(OrderConfirmedIntegrationEvent integrationEvent, CancellationToken ct = default)
    {
        await sender.SendCommandAsync<IssueInvoiceCommand, B2BInvoiceDto>(
            new IssueInvoiceCommand(
                integrationEvent.OrderId,
                integrationEvent.OrderNumber,
                integrationEvent.BuyerCompanyName,
                integrationEvent.BuyerVatNumber,
                integrationEvent.TotalAmountHt,
                integrationEvent.PaymentTermsDays), ct);
    }
}

public static class InvoicingServiceRegistration
{
    public static IServiceCollection AddInvoicingMicroservice(this IServiceCollection services)
    {
        services.AddSingleton<InvoicingDataStore>();
        services.AddSingleton<IMapProfile<B2BInvoice, B2BInvoiceDto>, InvoiceToDtoProfile>();
        services.AddTransient<ICommandHandler<IssueInvoiceCommand, B2BInvoiceDto>, IssueInvoiceCommandHandler>();
        services.AddTransient<ICommandHandler<MarkInvoicePaidCommand, B2BInvoiceDto?>, MarkInvoicePaidCommandHandler>();
        services.AddTransient<IQueryHandler<GetInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>, GetInvoicesQueryHandler>();
        services.AddTransient<IQueryHandler<GetInvoiceByOrderIdQuery, B2BInvoiceDto?>, GetInvoiceByOrderIdQueryHandler>();
        services.AddTransient<IIntegrationEventHandler<OrderConfirmedIntegrationEvent>, OrderConfirmedInvoiceHandler>();
        return services;
    }
}
