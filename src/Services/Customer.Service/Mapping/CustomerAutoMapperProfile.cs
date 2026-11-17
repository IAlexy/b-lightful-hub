namespace Customer.Service.Mapping;

using AutoMapper;
using Customer.Service.DataModels;
using Customer.Service.Entities;

/// <summary>
/// AutoMapper Profile mapping across the 4 Customer Microservice layers:
/// Commands -> DataModels <-> EF Core SQL Entities (CustomerProfileEntity, CustomerCustomTagEntity) -> DTOs.
/// </summary>
public sealed class CustomerAutoMapperProfile : Profile
{
    public CustomerAutoMapperProfile()
    {
        // 1. Custom Tag mappings (Input -> DataModel <-> SQL Entity -> DTO)
        CreateMap<CustomTagInput, CustomTagDataModel>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()))
            .ForMember(d => d.Value, opt => opt.MapFrom(s => string.IsNullOrWhiteSpace(s.Value) ? null : s.Value.Trim()))
            .ForMember(d => d.Color, opt => opt.MapFrom(s => string.IsNullOrWhiteSpace(s.Color) ? "#7C3AED" : s.Color.Trim()));

        CreateMap<CustomTagDataModel, CustomerCustomTagEntity>();
        CreateMap<CustomerCustomTagEntity, CustomTagDataModel>();
        CreateMap<CustomTagDataModel, CustomTagDto>();

        // 2. Customer Create & Update Commands -> CustomerAccountDataModel
        CreateMap<CreateB2BCustomerCommand, CustomerAccountDataModel>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name))
            .ForMember(d => d.SirenSiret, opt => opt.MapFrom(s => s.SirenSiret))
            .ForMember(d => d.InvoicingAddress, opt => opt.MapFrom(s => s.InvoicingAddress))
            .ForMember(d => d.UseInvoicingAddressForDelivery, opt => opt.MapFrom(s => s.UseInvoicingAddressForDelivery))
            .ForMember(d => d.DeliveryAddress, opt => opt.MapFrom(s =>
                s.UseInvoicingAddressForDelivery ? s.InvoicingAddress : (s.DeliveryAddress ?? string.Empty)))
            .ForMember(d => d.ContactEmail, opt => opt.MapFrom(s => s.ContactEmail ?? string.Empty))
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags ?? Array.Empty<CustomTagInput>()))
            .AfterMap((_, dest, _) => dest.ApplyBusinessRulesAndValidate());

        CreateMap<UpdateB2BCustomerCommand, CustomerAccountDataModel>()
            .ForMember(d => d.Id, opt => opt.MapFrom(s => s.CustomerId))
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name))
            .ForMember(d => d.SirenSiret, opt => opt.MapFrom(s => s.SirenSiret))
            .ForMember(d => d.InvoicingAddress, opt => opt.MapFrom(s => s.InvoicingAddress))
            .ForMember(d => d.UseInvoicingAddressForDelivery, opt => opt.MapFrom(s => s.UseInvoicingAddressForDelivery))
            .ForMember(d => d.DeliveryAddress, opt => opt.MapFrom(s =>
                s.UseInvoicingAddressForDelivery ? s.InvoicingAddress : (s.DeliveryAddress ?? string.Empty)))
            .ForMember(d => d.ContactEmail, opt => opt.MapFrom(s => s.ContactEmail ?? string.Empty))
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags ?? Array.Empty<CustomTagInput>()))
            .AfterMap((_, dest, _) => dest.ApplyBusinessRulesAndValidate());

        // 3. Domain DataModel <-> EF Core SQL Entity
        CreateMap<CustomerAccountDataModel, CustomerProfileEntity>()
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags));

        CreateMap<CustomerProfileEntity, CustomerAccountDataModel>()
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags));

        // 4. Domain DataModel -> Output DTO
        CreateMap<CustomerAccountDataModel, B2BCustomerDto>()
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags));
    }
}
