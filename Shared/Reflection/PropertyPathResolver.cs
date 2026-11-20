using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AkerMcp.Shared.Serialization;

namespace AkerMcp.Shared.Reflection
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

            object? current = root;
            foreach (var segment in SplitPath(path))
            {
                if (current == null) return null;
                current = ResolveSingle(current, segment);
            }
            return current;
        }

        public void Set(object root, string path, object? value)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path cannot be empty", nameof(path));

            var segments = SplitPath(path);

            if (segments.Count == 1)
            {
                SetMember(root, segments[0], value);
                return;
            }

            var chain = new object?[segments.Count];
            chain[0] = root;

            for (int i = 0; i < segments.Count - 1; i++)
            {
                if (chain[i] == null)
                    throw new PropertyPathException($"'{string.Join(".", segments.Take(i))}' is null");
                chain[i + 1] = ResolveSingle(chain[i]!, segments[i]);
            }

            var parent = chain[segments.Count - 1];
            if (parent == null)
                throw new PropertyPathException($"Parent object at '{string.Join(".", segments.Take(segments.Count - 1))}' is null");

            SetMember(parent, segments[segments.Count - 1], value);

            PropagateValueTypes(chain, segments);
        }

        /// <summary>The declared type at the end of <paramref name="path"/>, indexers included, or null when it does not resolve.</summary>
        public Type? GetTargetType(Type rootType, string path)
        {
            Type? current = rootType;
            foreach (var segment in SplitPath(path))
            {
                current = SegmentType(current, segment);
                if (current == null) return null;
            }
            return current;
        }

        private Type? SegmentType(Type type, string segment)
        {
            if (!TryParseIndexer(segment, out var member, out _, out var quoted))
                return _cache.GetMemberType(type, segment);

            var collectionType = member.Length == 0 ? type : _cache.GetMemberType(type, member);
            return collectionType == null ? null : IndexedType(collectionType, quoted);
        }

        // ---- path syntax ----

        /// <summary>
        /// The segments of a path, split on the dots outside an index: <c>dict["a.b"].x</c> is two segments. Split('.') cut
        /// the key in half and looked for a member called <c>dict["a</c>.
        /// </summary>
        internal static List<string> SplitPath(string path)
        {
            var segments = new List<string>();
            int start = 0;
            bool inIndex = false, inQuote = false;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (inQuote)
                {
                    if (c == '"') inQuote = false;
                    continue;
                }
                if (c == '"' && inIndex) inQuote = true;
                else if (c == '[') inIndex = true;
                else if (c == ']') inIndex = false;
                else if (c == '.' && !inIndex)
                {
                    segments.Add(path.Substring(start, i - start));
                    start = i + 1;
                }
            }
            segments.Add(path.Substring(start));
            return segments;
        }

        private static readonly Regex IndexerPattern = new Regex(@"^(\w*)\[(-?\d+|""[^""]*"")\]$", RegexOptions.Compiled);

        /// <summary><c>items[2]</c>, <c>[0]</c> or <c>dict["key"]</c>: the member (empty for a bare index) and the key.</summary>
        private static bool TryParseIndexer(string segment, out string member, out string key, out bool quoted)
        {
            member = key = "";
            quoted = false;
            if (segment.IndexOf('[') < 0) return false;
            var match = IndexerPattern.Match(segment);
            if (!match.Success) return false;
            member = match.Groups[1].Value;
            var raw = match.Groups[2].Value;
            quoted = raw.StartsWith("\"", StringComparison.Ordinal);
            key = quoted ? raw.Substring(1, raw.Length - 2) : raw;
            return true;
        }

        // ---- reading ----

        private object? ResolveSingle(object current, string segment)
        {
            if (TryParseIndexer(segment, out var member, out var key, out var quoted))
            {
                var collection = member.Length == 0 ? current : ResolveMember(current, member);
                if (collection == null)
                    throw new PropertyPathException($"'{member}' is null, so '{segment}' cannot be read");
                return ReadIndexed(collection, member, key, quoted);
            }
            return ResolveMember(current, segment);
        }

        private object? ResolveMember(object current, string name)
        {
            var type = current.GetType();

            var prop = _cache.GetProperty(type, name);
            if (prop != null && prop.CanRead)
            {
                try { return prop.GetValue(current); }
                catch (Exception ex)
                {
                    var cause = Unwrapped(ex);
                    throw new PropertyPathException(
                        $"Property '{name}' on {type.Name} threw on read: {cause.GetType().Name}: {cause.Message}");
                }
            }

            var field = _cache.GetField(type, name);
            if (field != null)
                return field.GetValue(current);

            throw new PropertyPathException(
                $"Cannot resolve '{name}' on type {type.Name}. Available members: " +
                string.Join(", ", GetMemberNames(type)));
        }

        private object? ReadIndexed(object collection, string member, string key, bool quoted)
        {
            if (collection is IDictionary dict)
            {
                var dictKey = DictionaryKey(collection.GetType(), key, quoted);
                if (!dict.Contains(dictKey))
                    throw new PropertyPathException($"{Named(member, collection)} has no key {Shown(key, quoted)}");
                return dict[dictKey];
            }
            if (!quoted && collection is IList list && !(collection is Array { Rank: > 1 }))
                return list[CheckedIndex(key, list.Count, member, collection)];

            var indexer = IndexerOf(collection.GetType(), quoted);
            if (indexer != null && indexer.CanRead)
            {
                try { return indexer.GetValue(collection, new[] { IndexArgument(indexer, key, quoted) }); }
                catch (Exception ex) { throw new PropertyPathException($"{Named(member, collection)}[{Shown(key, quoted)}]: {Unwrapped(ex).Message}"); }
            }
            throw new PropertyPathException($"{Named(member, collection)} is a {collection.GetType().Name}, which has no [{(quoted ? "key" : "index")}]");
        }

        // ---- writing ----

        private void SetMember(object target, string memberName, object? value)
        {
            // Handle indexer set: "items[2]", "[0]" or "dict[\"key\"]"
            if (TryParseIndexer(memberName, out var member, out var key, out var quoted))
            {
                var collection = member.Length == 0 ? target : ResolveMember(target, member);
                if (collection == null)
                    throw new PropertyPathException($"'{member}' is null, so '{memberName}' cannot be set");
                SetIndexed(collection, member, key, quoted, value);
                // A struct with an indexer of its own (Vector3[0]) was changed as a copy: it goes back where it came from.
                if (member.Length > 0 && collection.GetType().IsValueType)
                    SetMember(target, member, collection);
                return;
            }

            var type = target.GetType();

            var prop = _cache.GetProperty(type, memberName);
            if (prop != null)
            {
                if (!prop.CanWrite)
                    throw new PropertyPathException($"Property '{memberName}' on {type.Name} is read-only");
                var converted = ConvertValue(value, prop.PropertyType);
                Unwrap(() => prop.SetValue(target, converted));
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

        /// <summary>
        /// A write through an index, converted to the element type. The value used to go in as it came: any element type
        /// but string failed (set_property hands untyped values over as text), and an index past the end answered "Cannot
        /// find writable member 'items[5]'", as if the list were not there.
        /// </summary>
        private void SetIndexed(object collection, string member, string key, bool quoted, object? value)
        {
            if (collection is IDictionary dict)
            {
                if (dict.IsReadOnly)
                    throw new PropertyPathException($"{Named(member, collection)} is read-only");
                var dictKey = DictionaryKey(collection.GetType(), key, quoted);
                dict[dictKey] = ConvertValue(value, IndexedType(collection.GetType(), quoted) ?? typeof(object));
                return;
            }
            if (!quoted && collection is IList list && !(collection is Array { Rank: > 1 }))
            {
                int index = CheckedIndex(key, list.Count, member, collection);
                if (list.IsReadOnly && !(collection is Array))
                    throw new PropertyPathException($"{Named(member, collection)} is read-only");
                var converted = ConvertValue(value, IndexedType(collection.GetType(), quoted) ?? typeof(object));
                Unwrap(() => list[index] = converted);
                return;
            }

            var indexer = IndexerOf(collection.GetType(), quoted);
            if (indexer != null && indexer.CanWrite)
            {
                var converted = ConvertValue(value, indexer.PropertyType);
                var argument = IndexArgument(indexer, key, quoted);
                Unwrap(() => indexer.SetValue(collection, converted, new[] { argument }));
                return;
            }
            throw new PropertyPathException($"{Named(member, collection)} is a {collection.GetType().Name}, which has no writable [{(quoted ? "key" : "index")}]");
        }

        /// <summary>
        /// Writes every struct on the path back into what holds it, innermost first: a struct is read as a copy, and setting
        /// <c>points[0].x</c> changed the copy only. Only a member typed as a struct was written back, and an element reached
        /// through an index is not a member: on a Vec3[] or a List&lt;Vec3&gt; the write was dropped while set_property
        /// reported success.
        /// </summary>
        private void PropagateValueTypes(object?[] chain, List<string> segments)
        {
            for (int i = segments.Count - 1; i >= 1; i--)
            {
                var holder = chain[i - 1];
                var child = chain[i];
                if (holder == null || child == null || !child.GetType().IsValueType) continue;

                var slotType = SegmentType(holder.GetType(), segments[i - 1]);
                if (slotType != null && slotType.IsValueType)
                    SetMember(holder, segments[i - 1], child);
            }
        }

        // ---- collections ----

        /// <summary>The type an index of <paramref name="collectionType"/> reads: an array's element, IList&lt;T&gt;'s T, a dictionary's value.</summary>
        private static Type? IndexedType(Type collectionType, bool quoted)
        {
            if (collectionType.IsArray) return quoted ? null : collectionType.GetElementType();
            var dictionary = GenericInterface(collectionType, typeof(IDictionary<,>)) ?? GenericInterface(collectionType, typeof(IReadOnlyDictionary<,>));
            if (dictionary != null) return dictionary.GetGenericArguments()[1];
            if (!quoted)
            {
                var list = GenericInterface(collectionType, typeof(IList<>)) ?? GenericInterface(collectionType, typeof(IReadOnlyList<>));
                if (list != null) return list.GetGenericArguments()[0];
            }
            return IndexerOf(collectionType, quoted)?.PropertyType;
        }

        private static Type? GenericInterface(Type type, Type definition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == definition) return type;
            return type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);
        }

        /// <summary>A public indexer of one int (unquoted) or string (quoted) parameter: Vector3[0], a custom collection.</summary>
        private static PropertyInfo? IndexerOf(Type type, bool quoted)
        {
            var keyType = quoted ? typeof(string) : typeof(int);
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.GetIndexParameters() is { Length: 1 } index && index[0].ParameterType == keyType);
        }

        private static object IndexArgument(PropertyInfo indexer, string key, bool quoted) =>
            quoted ? (object)key : ParseIndex(key);

        private static object DictionaryKey(Type dictionaryType, string key, bool quoted)
        {
            var generic = GenericInterface(dictionaryType, typeof(IDictionary<,>)) ?? GenericInterface(dictionaryType, typeof(IReadOnlyDictionary<,>));
            if (generic == null) return quoted ? key : (object)ParseIndex(key);
            var keyType = generic.GetGenericArguments()[0];
            if (keyType == typeof(string) || keyType == typeof(object)) return key;
            return ConvertValue(key, keyType)!;
        }

        private static int ParseIndex(string key)
        {
            if (int.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index)) return index;
            throw new PropertyPathException($"[{key}] is not an index");
        }

        private static int CheckedIndex(string key, int count, string member, object collection)
        {
            int index = ParseIndex(key);
            if (index < 0 || index >= count)
                throw new PropertyPathException(
                    $"Index {index} is out of range: {Named(member, collection)} has {count} element{(count == 1 ? "" : "s")}");
            return index;
        }

        private static string Named(string member, object collection) =>
            member.Length > 0 ? $"'{member}'" : $"the {collection.GetType().Name}";

        private static string Shown(string key, bool quoted) => quoted ? $"\"{key}\"" : key;

        // ---- values ----

        /// <summary>
        /// <paramref name="value"/> as <paramref name="targetType"/>. set_property hands a value over as its JSON text when
        /// it cannot tell the member's type from the node's object — on Unity that object is the GameObject, which has
        /// neither "position" nor "BoxCollider.size" nor a component's list — and here the real type is known, so here the
        /// text becomes the value, through the serializer: vectors, colours, enums by name, arrays, lists, a StringName from
        /// a string, numbers in the invariant culture (under it-IT Convert.ChangeType read "0.5" as 5). A JSON value is
        /// converted the same way; a number or a boolean must fit, or it is an error (300 into a byte is not 44).
        /// Public for a node that writes through a channel of its own once it knows the member's type (Stride's Quantum).
        /// </summary>
        public static object? ConvertValue(object? value, Type targetType)
        {
            if (value is JsonElement json)
            {
                if (json.ValueKind == JsonValueKind.Null || json.ValueKind == JsonValueKind.Undefined) value = null;
                else return FromJson(json, targetType, json.ValueKind == JsonValueKind.String ? json.GetString() ?? "" : json.GetRawText());
            }

            if (value == null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    throw new PropertyValueException($"Cannot set null to non-nullable value type {targetType.Name}");
                return null;
            }

            if (targetType.IsInstanceOfType(value))
                return value;

            if (value is string text)
                return FromJson(ParseLenient(text), targetType, text);

            if (value is IConvertible)
                return FromJson(JsonSerializer.SerializeToElement(value, value.GetType(), ScalarOptions), targetType,
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

            throw new PropertyValueException($"Cannot convert value of type {value.GetType().Name} to {targetType.Name}");
        }

        private static object? FromJson(JsonElement json, Type targetType, string shown)
        {
            try
            {
                return Serializer.JsonElementToObject(json, targetType);
            }
            catch (Exception ex)
            {
                // The reason is the useful half ("missing z", "one of Skybox, SolidColor, …", "does not fit in Byte"): the
                // reply carries only the outer message, so it goes in there.
                throw new PropertyValueException($"Cannot convert '{Brief(shown)}' to {targetType.Name}: {Unwrapped(ex).Message}", ex);
            }
        }

        private static readonly GenericSerializer Serializer = new GenericSerializer();

        private static readonly JsonSerializerOptions ScalarOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private static string Brief(string text) => text.Length <= 60 ? text : text.Substring(0, 60) + "…";

        /// <summary>The text as JSON, or — when it is not JSON, as an enum name is not — the same text as a JSON string.</summary>
        internal static JsonElement ParseLenient(string text)
        {
            if (TryParseJson(text, out var element)) return element;
            return JsonSerializer.SerializeToElement(text);
        }

        internal static bool TryParseJson(string? text, out JsonElement element)
        {
            element = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                using var document = JsonDocument.Parse(text!);
                element = document.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static Exception Unwrapped(Exception ex) =>
            ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;

        /// <summary>A setter's own exception, not the TargetInvocationException reflection wraps it in.</summary>
        private static void Unwrap(Action write)
        {
            try { write(); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
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
