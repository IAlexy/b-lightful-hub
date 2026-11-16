namespace B2B.BuildingBlocks.Mapping;

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

/// <summary>
/// Strongly-typed mapping profile contract between <typeparamref name="TSource"/> and <typeparamref name="TDestination"/>.
/// </summary>
public interface IMapProfile<in TSource, out TDestination>
{
    TDestination Map(TSource source);
}

/// <summary>
/// High-performance Object Mapper supporting both explicit domain profiles (<see cref="IMapProfile{TSource,TDestination}"/>)
/// and cached compiled LINQ Expression Trees for convention-based mapping.
/// </summary>
public interface IB2BMapper
{
    TDestination Map<TSource, TDestination>(TSource source);
    IReadOnlyList<TDestination> MapList<TSource, TDestination>(IEnumerable<TSource> sources);
}

public sealed class B2BMapper(IServiceProvider serviceProvider) : IB2BMapper
{
    private static readonly ConcurrentDictionary<(Type Source, Type Dest), Delegate> CompiledMaps = new();

    public TDestination Map<TSource, TDestination>(TSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // 1. Prefer explicit domain mapping profile if registered in DI
        if (serviceProvider.GetService(typeof(IMapProfile<TSource, TDestination>)) is IMapProfile<TSource, TDestination> profile)
        {
            return profile.Map(source);
        }

        // 2. Fallback to compiled LINQ Expression Tree property mapper (zero reflection after first call)
        var fn = (Func<TSource, TDestination>)CompiledMaps.GetOrAdd(
            (typeof(TSource), typeof(TDestination)),
            static _ => CompileExpressionMap<TSource, TDestination>());

        return fn(source);
    }

    public IReadOnlyList<TDestination> MapList<TSource, TDestination>(IEnumerable<TSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources.Select(Map<TSource, TDestination>).ToList();
    }

    private static Func<TSource, TDestination> CompileExpressionMap<TSource, TDestination>()
    {
        var sourceParam = Expression.Parameter(typeof(TSource), "src");
        var sourceProps = typeof(TSource)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        // Support positional records (primary constructor) or mutable DTOs
        var ctors = typeof(TDestination).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var primaryCtor = ctors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();

        if (primaryCtor is not null && primaryCtor.GetParameters().Length > 0)
        {
            var args = primaryCtor.GetParameters().Select(param =>
            {
                if (param.Name is not null &&
                    sourceProps.TryGetValue(param.Name, out var srcProp) &&
                    param.ParameterType.IsAssignableFrom(srcProp.PropertyType))
                {
                    return (Expression)Expression.Property(sourceParam, srcProp);
                }

                return Expression.Default(param.ParameterType);
            });

            var newExpr = Expression.New(primaryCtor, args);
            return Expression.Lambda<Func<TSource, TDestination>>(newExpr, sourceParam).Compile();
        }

        var bindings = new List<MemberBinding>();
        foreach (var destProp in typeof(TDestination).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            if (sourceProps.TryGetValue(destProp.Name, out var srcProp) &&
                destProp.PropertyType.IsAssignableFrom(srcProp.PropertyType))
            {
                bindings.Add(Expression.Bind(destProp, Expression.Property(sourceParam, srcProp)));
            }
        }

        var memberInit = Expression.MemberInit(Expression.New(typeof(TDestination)), bindings);
        return Expression.Lambda<Func<TSource, TDestination>>(memberInit, sourceParam).Compile();
    }
}
