namespace Ordering.Service.Mapping;

using AutoMapper;
using Ordering.Service.DataModels;
using Ordering.Service.Entities;

/// <summary>
/// AutoMapper Profile mapping across the 4 Ordering layers:
/// Commands -> DataModels <-> EF Core SQL Entities (CustomerEntity, OrderEntity, OrderLineEntity) -> DTOs.
/// </summary>
public sealed class OrderingAutoMapperProfile : Profile
{
    public OrderingAutoMapperProfile()
    {
        // 1. Command Input -> Domain DataModels
        CreateMap<OrderLineRequest, OrderLineDataModel>()
            .ForMember(d => d.Sku, opt => opt.MapFrom(s => s.Sku.Trim().ToUpperInvariant()))
            .ForMember(d => d.Quantity, opt => opt.MapFrom(s => Math.Max(1, s.Quantity)));

        CreateMap<CreatePurchaseOrderCommand, CustomerDataModel>()
            .ForMember(d => d.CompanyName, opt => opt.MapFrom(s => s.BuyerCompanyName))
            .ForMember(d => d.VatNumber, opt => opt.MapFrom(s => s.BuyerVatNumber))
            .ForMember(d => d.ContactEmail, opt => opt.MapFrom(s => s.ContactEmail ?? $"procurement@{s.BuyerVatNumber.ToLowerInvariant()}.com"))
            .ForMember(d => d.BillingAddress, opt => opt.MapFrom(s => s.ShippingDestination));

        CreateMap<CreatePurchaseOrderCommand, OrderDataModel>()
            .ForMember(d => d.PaymentTermsDays, opt => opt.MapFrom(s => s.PaymentTermsDays <= 0 ? 30 : s.PaymentTermsDays))
            .ForMember(d => d.ShippingDestination, opt => opt.MapFrom(s => s.ShippingDestination))
            .ForMember(d => d.Status, opt => opt.MapFrom(s => s.AutoConfirm ? PurchaseOrderStatus.Confirmed : PurchaseOrderStatus.Draft))
            .ForMember(d => d.PaymentStatus, opt => opt.MapFrom(_ => OrderPaymentStatus.Unpaid))
            .ForMember(d => d.PaidAmountHt, opt => opt.MapFrom(_ => 0m))
            .ForMember(d => d.Lines, opt => opt.MapFrom(s => s.Items));

        // Cancellation Commands -> Cancellation DataModels
        CreateMap<CancelPurchaseOrderCommand, CancelOrderDataModel>()
            .ForMember(d => d.Reason, opt => opt.MapFrom(s => string.IsNullOrWhiteSpace(s.Reason) ? "Cancelled by B2B customer request" : s.Reason.Trim()));

        CreateMap<CancelOrderLineCommand, CancelOrderLineDataModel>()
            .ForMember(d => d.Reason, opt => opt.MapFrom(s => string.IsNullOrWhiteSpace(s.Reason) ? "Order line removed by B2B customer" : s.Reason.Trim()));

        // 2. Domain DataModels <-> EF Core SQL Database Entities
        CreateMap<CustomerDataModel, CustomerEntity>();
        CreateMap<CustomerEntity, CustomerDataModel>();

        CreateMap<OrderLineDataModel, OrderLineEntity>()
            .ForMember(d => d.LineTotalHt, opt => opt.MapFrom(s => s.LineTotalHt));
        CreateMap<OrderLineEntity, OrderLineDataModel>();

        CreateMap<OrderDataModel, OrderEntity>()
            .ForMember(d => d.Status, opt => opt.MapFrom(s => s.Status.ToString()))
            .ForMember(d => d.PaymentStatus, opt => opt.MapFrom(s => s.PaymentStatus.ToString()))
            .ForMember(d => d.PaidAmountHt, opt => opt.MapFrom(s => s.PaidAmountHt))
            .ForMember(d => d.TotalAmountHt, opt => opt.MapFrom(s => s.TotalAmountHt))
            .ForMember(d => d.CancellationReason, opt => opt.MapFrom(s => s.CancellationReason))
            .ForMember(d => d.CancelledAtUtc, opt => opt.MapFrom(s => s.CancelledAtUtc))
            .ForMember(d => d.Lines, opt => opt.MapFrom(s => s.Lines));

        CreateMap<OrderEntity, OrderDataModel>()
            .ForMember(d => d.Status, opt => opt.MapFrom(s =>
                Enum.TryParse<PurchaseOrderStatus>(s.Status, true, out var st) ? st : PurchaseOrderStatus.Confirmed))
            .ForMember(d => d.PaymentStatus, opt => opt.MapFrom(s =>
                Enum.TryParse<OrderPaymentStatus>(s.PaymentStatus, true, out var pst) ? pst : OrderPaymentStatus.Unpaid))
            .ForMember(d => d.PaidAmountHt, opt => opt.MapFrom(s => s.PaidAmountHt))
            .ForMember(d => d.CancellationReason, opt => opt.MapFrom(s => s.CancellationReason))
            .ForMember(d => d.CancelledAtUtc, opt => opt.MapFrom(s => s.CancelledAtUtc))
            .ForMember(d => d.Customer, opt => opt.MapFrom(s => s.Customer))
            .ForMember(d => d.Lines, opt => opt.MapFrom(s => s.Lines));

        // 3. Domain DataModels -> Output DTOs
        CreateMap<CustomerDataModel, CustomerDto>();
        CreateMap<OrderLineDataModel, PurchaseOrderLineDto>()
            .ForMember(d => d.LineTotalHt, opt => opt.MapFrom(s => s.LineTotalHt));

        CreateMap<OrderDataModel, PurchaseOrderDto>()
            .ForMember(d => d.BuyerCompanyName, opt => opt.MapFrom(s => s.Customer.CompanyName))
            .ForMember(d => d.BuyerVatNumber, opt => opt.MapFrom(s => s.Customer.VatNumber))
            .ForMember(d => d.Status, opt => opt.MapFrom(s => s.Status.ToString()))
            .ForMember(d => d.PaymentStatus, opt => opt.MapFrom(s => s.PaymentStatus.ToString()))
            .ForMember(d => d.PaidAmountHt, opt => opt.MapFrom(s => s.PaidAmountHt))
            .ForMember(d => d.TotalAmountHt, opt => opt.MapFrom(s => s.TotalAmountHt))
            .ForMember(d => d.TotalUnits, opt => opt.MapFrom(s => s.TotalUnits))
            .ForMember(d => d.CancellationReason, opt => opt.MapFrom(s => s.CancellationReason))
            .ForMember(d => d.CancelledAtUtc, opt => opt.MapFrom(s => s.CancelledAtUtc));

        CreateMap<OrderDataModel, OrderSummaryDto>()
            .ForMember(d => d.BuyerCompanyName, opt => opt.MapFrom(s => s.Customer.CompanyName))
            .ForMember(d => d.BuyerVatNumber, opt => opt.MapFrom(s => s.Customer.VatNumber))
            .ForMember(d => d.Status, opt => opt.MapFrom(s => s.Status.ToString()))
            .ForMember(d => d.PaymentStatus, opt => opt.MapFrom(s => s.PaymentStatus.ToString()))
            .ForMember(d => d.PaidAmountHt, opt => opt.MapFrom(s => s.PaidAmountHt))
            .ForMember(d => d.TotalAmountHt, opt => opt.MapFrom(s => s.TotalAmountHt))
            .ForMember(d => d.TotalUnits, opt => opt.MapFrom(s => s.TotalUnits))
            .ForMember(d => d.LineCount, opt => opt.MapFrom(s => s.Lines.Count));

        CreateMap<OrderDataModel, OrderCancellationResultDto>()
            .ForMember(d => d.OrderId, opt => opt.MapFrom(s => s.Id))
            .ForMember(d => d.Status, opt => opt.MapFrom(s => s.Status.ToString()))
            .ForMember(d => d.PaymentStatus, opt => opt.MapFrom(s => s.PaymentStatus.ToString()))
            .ForMember(d => d.RemainingTotalAmountHt, opt => opt.MapFrom(s => s.TotalAmountHt))
            .ForMember(d => d.RemainingUnits, opt => opt.MapFrom(s => s.TotalUnits))
            .ForMember(d => d.RemainingLineCount, opt => opt.MapFrom(s => s.Status == PurchaseOrderStatus.Cancelled ? 0 : s.Lines.Count))
            .ForMember(d => d.Order, opt => opt.MapFrom(s => s));
    }
}
