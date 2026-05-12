using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace MCPSharp.Shared.Reflection
{
    public class ReflectionCache
    {
        public static readonly ReflectionCache Instance = new ReflectionCache();

        private const BindingFlags PublicInstance =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        private readonly ConcurrentDictionary<(Type, string), PropertyInfo?> _propCache = new ConcurrentDictionary<(Type, string), PropertyInfo?>();
        private readonly ConcurrentDictionary<(Type, string), FieldInfo?> _fieldCache = new ConcurrentDictionary<(Type, string), FieldInfo?>();
        private readonly ConcurrentDictionary<(Type, string), Func<object, object?>> _getterCache = new ConcurrentDictionary<(Type, string), Func<object, object?>>();

        public PropertyInfo? GetProperty(Type type, string name)
            => _propCache.GetOrAdd((type, name), k => k.Item1.GetProperty(k.Item2, PublicInstance));

        public FieldInfo? GetField(Type type, string name)
            => _fieldCache.GetOrAdd((type, name), k => k.Item1.GetField(k.Item2, PublicInstance));

        public Type? GetMemberType(Type type, string name)
        {
            var prop = GetProperty(type, name);
            if (prop != null) return prop.PropertyType;

            var field = GetField(type, name);
            if (field != null) return field.FieldType;

            return null;
        }

        public Func<object, object?> GetCompiledGetter(Type type, string memberName)
            => _getterCache.GetOrAdd((type, memberName), k =>
            {
                var prop = GetProperty(k.Item1, k.Item2);
                if (prop != null && prop.CanRead)
                {
                    var param = Expression.Parameter(typeof(object));
                    var body = Expression.Convert(
                        Expression.Property(Expression.Convert(param, k.Item1), prop),
                        typeof(object));
                    return Expression.Lambda<Func<object, object?>>(body, param).Compile();
                }

                var field = GetField(k.Item1, k.Item2);
                if (field != null)
                {
                    var param = Expression.Parameter(typeof(object));
                    var body = Expression.Convert(
                        Expression.Field(Expression.Convert(param, k.Item1), field),
                        typeof(object));
                    return Expression.Lambda<Func<object, object?>>(body, param).Compile();
                }

                return _ => throw new PropertyPathException(
                    $"Cannot find readable member '{k.Item2}' on type {k.Item1.Name}");
            });
    }
}
