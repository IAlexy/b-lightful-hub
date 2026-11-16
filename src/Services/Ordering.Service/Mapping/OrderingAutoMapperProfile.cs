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
            .ForMember(d => d.Lines, opt => opt.MapFrom(s => s.Items));

        // 2. Domain DataModels <-> EF Core SQL Database Entities
        CreateMap<CustomerDataModel, CustomerEntity>();
        CreateMap<CustomerEntity, CustomerDataModel>();

        CreateMap<OrderLineDataModel, OrderLineEntity>()
            .ForMember(d => d.LineTotalHt, opt => opt.MapFrom(s => s.LineTotalHt));
        CreateMap<OrderLineEntity, OrderLineDataModel>();

        CreateMap<OrderDataModel, OrderEntity>()
            .ForMember(d => d.Status, opt => opt.MapFrom(s => s.Status.ToString()))
            .ForMember(d => d.TotalAmountHt, opt => opt.MapFrom(s => s.TotalAmountHt))
            .ForMember(d => d.Lines, opt => opt.MapFrom(s => s.Lines));

        CreateMap<OrderEntity, OrderDataModel>()
            .ForMember(d => d.Status, opt => opt.MapFrom(s =>
                Enum.TryParse<PurchaseOrderStatus>(s.Status, true, out var st) ? st : PurchaseOrderStatus.Confirmed))
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
            .ForMember(d => d.TotalAmountHt, opt => opt.MapFrom(s => s.TotalAmountHt))
            .ForMember(d => d.TotalUnits, opt => opt.MapFrom(s => s.TotalUnits));

        CreateMap<OrderDataModel, OrderSummaryDto>()
            .ForMember(d => d.BuyerCompanyName, opt => opt.MapFrom(s => s.Customer.CompanyName))
            .ForMember(d => d.BuyerVatNumber, opt => opt.MapFrom(s => s.Customer.VatNumber))
            .ForMember(d => d.Status, opt => opt.MapFrom(s => s.Status.ToString()))
            .ForMember(d => d.TotalAmountHt, opt => opt.MapFrom(s => s.TotalAmountHt))
            .ForMember(d => d.TotalUnits, opt => opt.MapFrom(s => s.TotalUnits))
            .ForMember(d => d.LineCount, opt => opt.MapFrom(s => s.Lines.Count));
    }
}
