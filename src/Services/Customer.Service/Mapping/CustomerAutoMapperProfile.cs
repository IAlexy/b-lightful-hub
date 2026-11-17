namespace Customer.Service.Mapping;

using AutoMapper;
using Customer.Service.DataModels;
using Customer.Service.Entities;

/// <summary>
/// AutoMapper Profile performing pure structural mappings across:
/// Commands / Queries -> DataModels <-> EF Core SQL Entities (CustomerProfileEntity, CustomTagEntity, CustomerTagAssignmentEntity) -> DTOs.
/// </summary>
public sealed class CustomerAutoMapperProfile : Profile
{
    public CustomerAutoMapperProfile()
    {
        // 1. Custom Tag mappings (Command/Input -> DataModel <-> SQL Entity -> DTO)
        CreateMap<CustomTagInput, CustomTagDataModel>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.Value, opt => opt.MapFrom(s => s.Value))
            .ForMember(d => d.Color, opt => opt.MapFrom(s => s.Color ?? "#7C3AED"));

        CreateMap<CreateCustomTagCommand, CustomTagDataModel>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.Value, opt => opt.MapFrom(s => s.Value))
            .ForMember(d => d.Color, opt => opt.MapFrom(s => s.Color ?? "#7C3AED"));

        CreateMap<CustomTagDataModel, CustomTagEntity>();
        CreateMap<CustomTagEntity, CustomTagDataModel>();
        CreateMap<CustomTagDataModel, CustomTagDto>();

        // 2. Tag Assignment Commands -> CustomerTagAssignmentDataModel
        CreateMap<AssignTagToCustomerCommand, CustomerTagAssignmentDataModel>();
        CreateMap<RemoveTagFromCustomerCommand, CustomerTagAssignmentDataModel>();
        CreateMap<CustomerTagAssignmentDataModel, CustomerTagAssignmentEntity>();

        // 3. Customer Create & Update Commands -> CustomerAccountDataModel
        CreateMap<CreateB2BCustomerCommand, CustomerAccountDataModel>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.SirenSiret, opt => opt.MapFrom(s => s.SirenSiret ?? string.Empty))
            .ForMember(d => d.InvoicingAddress, opt => opt.MapFrom(s => s.InvoicingAddress ?? string.Empty))
            .ForMember(d => d.DeliveryAddress, opt => opt.MapFrom(s => s.DeliveryAddress ?? string.Empty))
            .ForMember(d => d.UseInvoicingAddressForDelivery, opt => opt.MapFrom(s => s.UseInvoicingAddressForDelivery))
            .ForMember(d => d.ContactEmail, opt => opt.MapFrom(s => s.ContactEmail))
            .ForMember(d => d.PhoneNumber, opt => opt.MapFrom(s => s.PhoneNumber))
            .ForMember(d => d.TagIds, opt => opt.MapFrom(s => s.TagIds != null ? s.TagIds.ToList() : new List<Guid>()))
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags ?? Array.Empty<CustomTagInput>()));

        CreateMap<UpdateB2BCustomerCommand, CustomerAccountDataModel>()
            .ForMember(d => d.Id, opt => opt.MapFrom(s => s.CustomerId))
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name ?? string.Empty))
            .ForMember(d => d.SirenSiret, opt => opt.MapFrom(s => s.SirenSiret ?? string.Empty))
            .ForMember(d => d.InvoicingAddress, opt => opt.MapFrom(s => s.InvoicingAddress ?? string.Empty))
            .ForMember(d => d.DeliveryAddress, opt => opt.MapFrom(s => s.DeliveryAddress ?? string.Empty))
            .ForMember(d => d.UseInvoicingAddressForDelivery, opt => opt.MapFrom(s => s.UseInvoicingAddressForDelivery))
            .ForMember(d => d.ContactEmail, opt => opt.MapFrom(s => s.ContactEmail))
            .ForMember(d => d.PhoneNumber, opt => opt.MapFrom(s => s.PhoneNumber))
            .ForMember(d => d.TagIds, opt => opt.MapFrom(s => s.TagIds != null ? s.TagIds.ToList() : new List<Guid>()))
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags ?? Array.Empty<CustomTagInput>()));

        // 4. Query Objects -> Query DataModels
        CreateMap<GetB2BCustomersQuery, CustomerFilterDataModel>();
        CreateMap<GetB2BCustomerByIdQuery, CustomerIdLookupDataModel>();
        CreateMap<GetCustomerOrdersQuery, CustomerIdLookupDataModel>();
        CreateMap<GetCustomerInvoicesQuery, CustomerIdLookupDataModel>();
        CreateMap<GetCustomerDeliveriesQuery, CustomerIdLookupDataModel>();
        CreateMap<GetCustomerFavoriteProductsQuery, CustomerIdLookupDataModel>();

        // 5. Domain DataModel <-> EF Core SQL Entity (CustomerProfileEntity + TagAssignments)
        CreateMap<CustomerAccountDataModel, CustomerProfileEntity>()
            .ForMember(d => d.TagAssignments, opt => opt.Ignore());

        CreateMap<CustomerProfileEntity, CustomerAccountDataModel>()
            .ForMember(d => d.TagIds, opt => opt.MapFrom(s =>
                s.TagAssignments.Select(a => a.TagId).ToList()))
            .ForMember(d => d.Tags, opt => opt.MapFrom(s =>
                s.TagAssignments.Where(a => a.Tag != null).Select(a => a.Tag).ToList()));

        // 6. Domain DataModel -> Output DTO
        CreateMap<CustomerAccountDataModel, B2BCustomerDto>()
            .ForMember(d => d.Tags, opt => opt.MapFrom(s => s.Tags));
    }
}
