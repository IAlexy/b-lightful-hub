namespace AutoMapper;

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

public interface IMappingOperationOptions<out TSource>
{
    void MapFrom<TMember>(Func<TSource, TMember> resolver);
    void Ignore();
}

public interface IMappingExpression<TSource, TDestination>
{
    IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMappingOperationOptions<TSource>> memberOptions);

    IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, IResolutionContext, TDestination> ctor);

    IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination, IResolutionContext> afterFunction);
}

public interface IResolutionContext
{
    TDestination Map<TDestination>(object source);
    TDestination Map<TSource, TDestination>(TSource source);
}

public interface IMapper
{
    TDestination Map<TDestination>(object source);
    TDestination Map<TSource, TDestination>(TSource source);
}

internal interface ITypeMapExecutor
{
    object Execute(object source, IResolutionContext context);
}

internal sealed class MappingOperationOptions<TSource> : IMappingOperationOptions<TSource>
{
    public Func<TSource, object?>? Resolver { get; private set; }
    public bool IsIgnored { get; private set; }

    public void MapFrom<TMember>(Func<TSource, TMember> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Resolver = src => resolver(src);
    }

    public void Ignore() => IsIgnored = true;
}

internal sealed class TypeMapConfiguration<TSource, TDestination> : IMappingExpression<TSource, TDestination>, ITypeMapExecutor
{
    private readonly Dictionary<string, MappingOperationOptions<TSource>> _memberConfigs = new(StringComparer.OrdinalIgnoreCase);
    private Func<TSource, IResolutionContext, TDestination>? _customCtor;
    private readonly List<Action<TSource, TDestination, IResolutionContext>> _afterMaps = [];
    private Func<TSource, TDestination>? _compiledFallback;

    public IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMappingOperationOptions<TSource>> memberOptions)
    {
        ArgumentNullException.ThrowIfNull(destinationMember);
        ArgumentNullException.ThrowIfNull(memberOptions);

        var memberName = ExtractMemberName(destinationMember.Body);
        var opts = new MappingOperationOptions<TSource>();
        memberOptions(opts);
        _memberConfigs[memberName] = opts;
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, IResolutionContext, TDestination> ctor)
    {
        ArgumentNullException.ThrowIfNull(ctor);
        _customCtor = ctor;
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination, IResolutionContext> afterFunction)
    {
        ArgumentNullException.ThrowIfNull(afterFunction);
        _afterMaps.Add(afterFunction);
        return this;
    }

    public object Execute(object source, IResolutionContext context)
    {
        var typedSource = (TSource)source;
        TDestination dest;

        if (_customCtor is not null)
        {
            dest = _customCtor(typedSource, context);
        }
        else
        {
            dest = CreateAndPopulate(typedSource, context);
        }

        foreach (var after in _afterMaps)
        {
            after(typedSource, dest, context);
        }

        return dest!;
    }

    private TDestination CreateAndPopulate(TSource source, IResolutionContext context)
    {
        var sourceProps = typeof(TSource)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        var ctors = typeof(TDestination).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var primaryCtor = ctors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();

        // 1. Positional record / constructor-based target
        if (primaryCtor is not null && primaryCtor.GetParameters().Length > 0)
        {
            var parameters = primaryCtor.GetParameters();
            var args = new object?[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var paramName = param.Name ?? string.Empty;

                if (_memberConfigs.TryGetValue(paramName, out var cfg))
                {
                    args[i] = cfg.IsIgnored ? GetDefaultValue(param.ParameterType) : ConvertValue(cfg.Resolver?.Invoke(source), param.ParameterType, context);
                }
                else if (sourceProps.TryGetValue(paramName, out var srcProp))
                {
                    args[i] = ConvertValue(srcProp.GetValue(source), param.ParameterType, context);
                }
                else
                {
                    args[i] = GetDefaultValue(param.ParameterType);
                }
            }

            return (TDestination)primaryCtor.Invoke(args);
        }

        // 2. Mutable class / entity target
        _compiledFallback ??= CompileDefaultCtor<TDestination>();
        var instance = _compiledFallback(source);

        foreach (var destProp in typeof(TDestination).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            if (_memberConfigs.TryGetValue(destProp.Name, out var cfg))
            {
                if (!cfg.IsIgnored && cfg.Resolver is not null)
                {
                    var val = ConvertValue(cfg.Resolver(source), destProp.PropertyType, context);
                    destProp.SetValue(instance, val);
                }
            }
            else if (sourceProps.TryGetValue(destProp.Name, out var srcProp))
            {
                var val = ConvertValue(srcProp.GetValue(source), destProp.PropertyType, context);
                destProp.SetValue(instance, val);
            }
        }

        return instance;
    }

    private static object? ConvertValue(object? rawValue, Type targetType, IResolutionContext context)
    {
        if (rawValue is null)
        {
            return GetDefaultValue(targetType);
        }

        var srcType = rawValue.GetType();
        if (targetType.IsAssignableFrom(srcType))
        {
            return rawValue;
        }

        if (targetType == typeof(string) && srcType.IsEnum)
        {
            return rawValue.ToString();
        }

        if (targetType.IsEnum && rawValue is string strVal && Enum.TryParse(targetType, strVal, true, out var parsedEnum))
        {
            return parsedEnum;
        }

        // Handle collection mapping automatically (e.g. IEnumerable<TSrcItem> -> List<TDestItem> / IReadOnlyList<TDestItem>)
        if (rawValue is System.Collections.IEnumerable enumerable &&
            targetType.IsGenericType &&
            typeof(System.Collections.IEnumerable).IsAssignableFrom(targetType))
        {
            var destElementType = targetType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(destElementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;

            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    var mappedItem = destElementType.IsInstanceOfType(item)
                        ? item
                        : MapDynamic(context, item, destElementType);
                    list.Add(mappedItem);
                }
            }

            return list;
        }

        // Nested complex object mapping
        return MapDynamic(context, rawValue, targetType);
    }

    private static object MapDynamic(IResolutionContext context, object item, Type targetType)
    {
        var mapMethod = typeof(IResolutionContext)
            .GetMethods()
            .First(m => m.Name == nameof(IResolutionContext.Map) && m.GetGenericArguments().Length == 1)
            .MakeGenericMethod(targetType);

        return mapMethod.Invoke(context, [item])!;
    }

    private static Func<TSource, TDest> CompileDefaultCtor<TDest>()
    {
        var param = Expression.Parameter(typeof(TSource), "_");
        var newExpr = Expression.New(typeof(TDest));
        return Expression.Lambda<Func<TSource, TDest>>(newExpr, param).Compile();
    }

    private static object? GetDefaultValue(Type t) =>
        t.IsValueType ? Activator.CreateInstance(t) : null;

    private static string ExtractMemberName(Expression body) =>
        body switch
        {
            MemberExpression m => m.Member.Name,
            UnaryExpression { Operand: MemberExpression um } => um.Member.Name,
            _ => throw new ArgumentException("Expression must be a property or field access.")
        };
}

public abstract class Profile
{
    internal Dictionary<(Type Source, Type Dest), ITypeMapExecutor> TypeMaps { get; } = [];

    protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
    {
        var config = new TypeMapConfiguration<TSource, TDestination>();
        TypeMaps[(typeof(TSource), typeof(TDestination))] = config;
        return config;
    }
}

public sealed class MapperConfiguration
{
    private readonly ConcurrentDictionary<(Type Source, Type Dest), ITypeMapExecutor> _registry = new();

    public MapperConfiguration(Action<IMapperConfigurationExpression> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var expr = new MapperConfigurationExpression();
        configure(expr);

        foreach (var profile in expr.Profiles)
        {
            foreach (var kv in profile.TypeMaps)
            {
                _registry[kv.Key] = kv.Value;
            }
        }
    }

    public IMapper CreateMapper() => new RuntimeMapper(_registry);

    internal ITypeMapExecutor GetOrCreateExecutor(Type sourceType, Type destType) =>
        _registry.GetOrAdd((sourceType, destType), static key =>
        {
            var closedType = typeof(TypeMapConfiguration<,>).MakeGenericType(key.Source, key.Dest);
            return (ITypeMapExecutor)Activator.CreateInstance(closedType)!;
        });
}

public interface IMapperConfigurationExpression
{
    void AddProfile<TProfile>() where TProfile : Profile, new();
    void AddProfile(Profile profile);
}

internal sealed class MapperConfigurationExpression : IMapperConfigurationExpression
{
    public List<Profile> Profiles { get; } = [];

    public void AddProfile<TProfile>() where TProfile : Profile, new() =>
        Profiles.Add(new TProfile());

    public void AddProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profiles.Add(profile);
    }
}

internal sealed class RuntimeMapper(ConcurrentDictionary<(Type Source, Type Dest), ITypeMapExecutor> registry)
    : IMapper, IResolutionContext
{
    public TDestination Map<TDestination>(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return (TDestination)MapInternal(source, source.GetType(), typeof(TDestination));
    }

    public TDestination Map<TSource, TDestination>(TSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return (TDestination)MapInternal(source, typeof(TSource), typeof(TDestination));
    }

    private object MapInternal(object source, Type sourceType, Type destType)
    {
        if (source is System.Collections.IEnumerable enumerable &&
            source is not string &&
            destType.IsGenericType &&
            typeof(System.Collections.IEnumerable).IsAssignableFrom(destType))
        {
            var destElementType = destType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(destElementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;

            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    list.Add(MapInternal(item, item.GetType(), destElementType));
                }
            }

            return list;
        }

        var executor = registry.GetOrAdd((sourceType, destType), static key =>
        {
            var closedType = typeof(TypeMapConfiguration<,>).MakeGenericType(key.Source, key.Dest);
            return (ITypeMapExecutor)Activator.CreateInstance(closedType)!;
        });

        return executor.Execute(source, this);
    }
}

public static class AutoMapperServiceCollectionExtensions
{
    public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Profile[] profiles)
    {
        foreach (var profile in profiles)
        {
            services.AddSingleton<Profile>(profile);
        }

        services.AddSingleton<MapperConfiguration>(sp =>
        {
            var registeredProfiles = sp.GetServices<Profile>();
            return new MapperConfiguration(cfg =>
            {
                foreach (var profile in registeredProfiles)
                {
                    cfg.AddProfile(profile);
                }
            });
        });

        services.AddSingleton<IMapper>(sp => sp.GetRequiredService<MapperConfiguration>().CreateMapper());
        return services;
    }
}
