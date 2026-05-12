using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AkerMcp.Shared.Serialization
{
    public class TypeRegistry
    {
        public static readonly TypeRegistry Instance = new TypeRegistry();

        private readonly ConcurrentDictionary<string, Type> _aliases = new ConcurrentDictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Type, Func<object, Dictionary<string, object?>>> _serializers = new ConcurrentDictionary<Type, Func<object, Dictionary<string, object?>>>();
        private readonly ConcurrentDictionary<Type, Func<Dictionary<string, object?>, object>> _deserializers = new ConcurrentDictionary<Type, Func<Dictionary<string, object?>, object>>();

        public void RegisterAlias(string alias, Type type)
        {
            _aliases[alias] = type;
        }

        public Type? ResolveAlias(string alias)
        {
            return _aliases.TryGetValue(alias, out var type) ? type : null;
        }

        public IEnumerable<string> GetRegisteredAliases() => _aliases.Keys;

        public void RegisterCustomSerializer<T>(
            Func<T, Dictionary<string, object?>> serialize,
            Func<Dictionary<string, object?>, T> deserialize) where T : notnull
        {
            _serializers[typeof(T)] = obj => serialize((T)obj);
            _deserializers[typeof(T)] = dict => deserialize(dict)!;
        }

        public bool HasCustomSerializer(Type type) => _serializers.ContainsKey(type);
        public bool HasCustomDeserializer(Type type) => _deserializers.ContainsKey(type);

        public Dictionary<string, object?>? TrySerialize(object value)
        {
            var type = value.GetType();
            if (_serializers.TryGetValue(type, out var serializer))
                return serializer(value);
            return null;
        }

        public object? TryDeserialize(Type targetType, Dictionary<string, object?> data)
        {
            if (_deserializers.TryGetValue(targetType, out var deserializer))
                return deserializer(data);
            return null;
        }
    }
}
