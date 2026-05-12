using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MCPSharp.Shared.Reflection
{
    public class PropertyPathResolver
    {
        private readonly ReflectionCache _cache;

        public PropertyPathResolver() : this(ReflectionCache.Instance) { }

        public PropertyPathResolver(ReflectionCache cache)
        {
            _cache = cache;
        }

        public object? Resolve(object root, string path)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path cannot be empty", nameof(path));

            var segments = path.Split('.');
            object? current = root;

            foreach (var segment in segments)
            {
                if (current == null) return null;

                if (TryResolveIndexer(current, segment, out var indexerResult))
                {
                    current = indexerResult;
                    continue;
                }

                var type = current.GetType();

                var prop = _cache.GetProperty(type, segment);
                if (prop != null && prop.CanRead)
                {
                    current = prop.GetValue(current);
                    continue;
                }

                var field = _cache.GetField(type, segment);
                if (field != null)
                {
                    current = field.GetValue(current);
                    continue;
                }

                throw new PropertyPathException(
                    $"Cannot resolve '{segment}' on type {type.Name}. Available members: " +
                    string.Join(", ", GetMemberNames(type)));
            }
            return current;
        }

        public void Set(object root, string path, object? value)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path cannot be empty", nameof(path));

            var segments = path.Split('.');

            if (segments.Length == 1)
            {
                SetMember(root, segments[0], value);
                return;
            }

            var chain = new object?[segments.Length];
            chain[0] = root;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (chain[i] == null)
                    throw new PropertyPathException($"Object at segment '{segments[i]}' is null");
                chain[i + 1] = ResolveSingle(chain[i]!, segments[i]);
            }

            var parent = chain[segments.Length - 1];
            if (parent == null)
                throw new PropertyPathException($"Parent object at '{string.Join(".", segments[..^1])}' is null");

            SetMember(parent, segments[^1], value);

            PropagateValueTypes(chain, segments);
        }

        public Type? GetTargetType(Type rootType, string path)
        {
            var segments = path.Split('.');
            var currentType = rootType;

            foreach (var segment in segments)
            {
                var memberType = _cache.GetMemberType(currentType, segment);
                if (memberType == null) return null;
                currentType = memberType;
            }
            return currentType;
        }

        private object? ResolveSingle(object current, string segment)
        {
            if (TryResolveIndexer(current, segment, out var result))
                return result;

            var type = current.GetType();

            var prop = _cache.GetProperty(type, segment);
            if (prop != null && prop.CanRead)
                return prop.GetValue(current);

            var field = _cache.GetField(type, segment);
            if (field != null)
                return field.GetValue(current);

            throw new PropertyPathException($"Cannot resolve '{segment}' on type {type.Name}");
        }

        private void SetMember(object target, string memberName, object? value)
        {
            // Handle indexer set: "items[2]" or "[0]"
            if (TrySetIndexer(target, memberName, value))
                return;

            var type = target.GetType();

            var prop = _cache.GetProperty(type, memberName);
            if (prop != null)
            {
                if (!prop.CanWrite)
                    throw new PropertyPathException($"Property '{memberName}' on {type.Name} is read-only");
                prop.SetValue(target, ConvertValue(value, prop.PropertyType));
                return;
            }

            var field = _cache.GetField(type, memberName);
            if (field != null)
            {
                if (field.IsInitOnly)
                    throw new PropertyPathException($"Field '{memberName}' on {type.Name} is read-only");
                field.SetValue(target, ConvertValue(value, field.FieldType));
                return;
            }

            throw new PropertyPathException($"Cannot find writable member '{memberName}' on type {type.Name}");
        }

        private bool TrySetIndexer(object target, string segment, object? value)
        {
            var match = Regex.Match(segment, @"^(\w+)?\[(\d+|""[^""]*"")\]$");
            if (!match.Success) return false;

            var propName = match.Groups[1].Value;
            var indexStr = match.Groups[2].Value;

            object? collection = target;
            if (!string.IsNullOrEmpty(propName))
                collection = ResolveSingle(target, propName);

            if (collection == null) return false;

            if (indexStr.StartsWith("\"") && indexStr.EndsWith("\""))
            {
                var key = indexStr.Trim('"');
                if (collection is IDictionary dict)
                {
                    dict[key] = value;
                    return true;
                }
            }
            else if (int.TryParse(indexStr, out var index))
            {
                if (collection is IList list)
                {
                    if (index >= 0 && index < list.Count)
                    {
                        list[index] = value;
                        return true;
                    }
                }
                if (collection is Array arr)
                {
                    if (index >= 0 && index < arr.Length)
                    {
                        arr.SetValue(value, index);
                        return true;
                    }
                }
            }

            return false;
        }

        private void PropagateValueTypes(object?[] chain, string[] segments)
        {
            for (int i = segments.Length - 1; i >= 1; i--)
            {
                var parent = chain[i - 1];
                var child = chain[i];
                if (parent == null || child == null) continue;

                var parentType = parent.GetType();
                var memberType = _cache.GetMemberType(parentType, segments[i - 1]);

                if (memberType != null && memberType.IsValueType)
                {
                    SetMember(parent, segments[i - 1], child);
                    if (i >= 2) chain[i - 1] = parent;
                }
            }
        }

        private bool TryResolveIndexer(object obj, string segment, out object? result)
        {
            result = null;
            var match = Regex.Match(segment, @"^(\w+)?\[(\d+|""[^""]*"")\]$");
            if (!match.Success) return false;

            var propName = match.Groups[1].Value;
            var indexStr = match.Groups[2].Value;

            object? target = obj;
            if (!string.IsNullOrEmpty(propName))
                target = ResolveSingle(obj, propName);

            if (target == null) return false;

            if (indexStr.StartsWith("\"") && indexStr.EndsWith("\""))
            {
                var key = indexStr.Trim('"');
                if (target is IDictionary dict)
                {
                    result = dict[key];
                    return true;
                }
            }
            else if (int.TryParse(indexStr, out var index))
            {
                if (target is IList list)
                {
                    result = list[index];
                    return true;
                }
                if (target is Array arr)
                {
                    result = arr.GetValue(index);
                    return true;
                }
            }

            return false;
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (value == null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    throw new PropertyPathException($"Cannot set null to non-nullable value type {targetType.Name}");
                return null;
            }

            var valueType = value.GetType();
            if (targetType.IsAssignableFrom(valueType))
                return value;

            try
            {
                return Convert.ChangeType(value, targetType);
            }
            catch (Exception ex)
            {
                throw new PropertyPathException(
                    $"Cannot convert value of type {valueType.Name} to {targetType.Name}", ex);
            }
        }

        private static string[] GetMemberNames(Type type)
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var names = new string[props.Length + fields.Length];
            for (int i = 0; i < props.Length; i++)
                names[i] = props[i].Name;
            for (int i = 0; i < fields.Length; i++)
                names[props.Length + i] = fields[i].Name;
            return names;
        }
    }
}
