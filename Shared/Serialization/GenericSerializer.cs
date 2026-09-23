using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MessagePack;
using MessagePack.Resolvers;

namespace AkerMcp.Shared.Serialization
{
    public class GenericSerializer
    {
        private readonly MessagePackSerializerOptions _msgpackOptions;
        private readonly TypeRegistry _typeRegistry;
        private readonly JsonSerializerOptions _jsonOptions;

        public GenericSerializer() : this(TypeRegistry.Instance) { }

        public GenericSerializer(TypeRegistry typeRegistry)
        {
            _typeRegistry = typeRegistry;
            _msgpackOptions = MessagePackSerializerOptions.Standard
                .WithResolver(ContractlessStandardResolver.Instance);
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        public byte[] SerializeToMsgpack<T>(T value)
        {
            return MessagePackSerializer.Serialize(value, _msgpackOptions);
        }

        public byte[] SerializeToMsgpack(Type type, object value)
        {
            return MessagePackSerializer.Serialize(type, value, _msgpackOptions);
        }

        public T DeserializeFromMsgpack<T>(byte[] data)
        {
            return MessagePackSerializer.Deserialize<T>(data, _msgpackOptions);
        }

        public object DeserializeFromMsgpack(Type type, byte[] data)
        {
            return MessagePackSerializer.Deserialize(type, data, _msgpackOptions)!;
        }

        #region JSON → Object

        public object? JsonElementToObject(JsonElement element, Type targetType)
        {
            if (element.ValueKind == JsonValueKind.Null)
                return null;

            if (element.ValueKind == JsonValueKind.Undefined)
                return null;

            // Nullable<T> — unwrap
            var underlyingNullable = Nullable.GetUnderlyingType(targetType);
            if (underlyingNullable != null)
                return JsonElementToObject(element, underlyingNullable);

            // Primitives
            if (targetType == typeof(string)) return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            
            if (targetType == typeof(int)) 
                return element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var i2) ? i2 : (element.TryGetInt32(out var i) ? i : (int)element.GetDouble());
            
            if (targetType == typeof(long)) 
                return element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var l2) ? l2 : (element.TryGetInt64(out var l) ? l : (long)element.GetDouble());
            
            if (targetType == typeof(float)) 
                return element.ValueKind == JsonValueKind.String && float.TryParse(element.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f2) ? f2 : (float)element.GetDouble();
            
            if (targetType == typeof(double)) 
                return element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2) ? d2 : element.GetDouble();
            
            if (targetType == typeof(bool)) 
                return element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var b2) ? b2 : element.GetBoolean();
            
            if (targetType == typeof(decimal)) 
                return element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec2) ? dec2 : element.GetDecimal();
            
            if (targetType == typeof(byte)) return (byte)(element.ValueKind == JsonValueKind.String ? byte.Parse(element.GetString()!) : element.GetInt32());
            if (targetType == typeof(short)) return (short)(element.ValueKind == JsonValueKind.String ? short.Parse(element.GetString()!) : element.GetInt32());
            if (targetType == typeof(uint)) return (uint)(element.ValueKind == JsonValueKind.String ? uint.Parse(element.GetString()!) : element.GetInt64());
            if (targetType == typeof(ulong)) return (ulong)(element.ValueKind == JsonValueKind.String ? ulong.Parse(element.GetString()!) : element.GetDouble());

            // A Guid, a date or a duration is one JSON value, as ObjectToJsonElement writes it
            if (IsJsonScalar(targetType))
                return JsonSerializer.Deserialize(element.GetRawText(), targetType, _jsonOptions);

            // Enum
            if (targetType.IsEnum)
                return DeserializeEnum(element, targetType);

            // Array
            if (targetType.IsArray)
                return DeserializeArray(element, targetType);

            // List<T>
            if (IsGenericList(targetType))
                return DeserializeList(element, targetType);

            // Dictionary<string, T>
            if (IsStringDictionary(targetType))
                return DeserializeDictionary(element, targetType);

            // Struct (custom registered first, then reflection)
            if (targetType.IsValueType && !targetType.IsPrimitive)
                return DeserializeStruct(element, targetType);

            // Class objects — try System.Text.Json
            try
            {
                return JsonSerializer.Deserialize(element.GetRawText(), targetType, _jsonOptions);
            }
            catch
            {
                return DeserializeObjectViaReflection(element, targetType);
            }
        }

        /// <summary>
        /// An enum by name (any case) or by number. A name the enum does not have, or a number it does not define, is an
        /// error: it used to become the enum's zero, so a typo ("Skybx") answered "set successfully" and switched the
        /// member to whatever 0 means. A [Flags] enum takes any combination of its bits, as it does in code.
        /// </summary>
        private static object DeserializeEnum(JsonElement element, Type enumType)
        {
            object? value = null;
            if (element.ValueKind == JsonValueKind.String)
            {
                var str = element.GetString();
                if (str != null && Enum.TryParse(enumType, str.Trim(), true, out var parsed))
                    value = parsed;
            }
            else if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
            {
                value = Enum.ToObject(enumType, number);
            }

            if (value != null && (enumType.IsDefined(typeof(FlagsAttribute), false) || Enum.IsDefined(enumType, value)))
                return value;

            var names = Enum.GetNames(enumType);
            var listed = names.Length <= 24 ? string.Join(", ", names) : string.Join(", ", names.Take(24)) + ", …";
            throw new FormatException($"{Brief(element)} is not a {enumType.Name}; it is one of {listed}");
        }

        private object DeserializeArray(JsonElement element, Type arrayType)
        {
            var elementType = arrayType.GetElementType()!;

            if (element.ValueKind != JsonValueKind.Array)
            {
                // Single value → array of one
                var singleArray = Array.CreateInstance(elementType, 1);
                singleArray.SetValue(JsonElementToObject(element, elementType), 0);
                return singleArray;
            }

            var jsonArray = element.EnumerateArray().ToList();
            var array = Array.CreateInstance(elementType, jsonArray.Count);

            for (int i = 0; i < jsonArray.Count; i++)
            {
                var item = JsonElementToObject(jsonArray[i], elementType);
                array.SetValue(item, i);
            }

            return array;
        }

        private object DeserializeList(JsonElement element, Type listType)
        {
            var elementType = listType.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(listType)!;

            if (element.ValueKind != JsonValueKind.Array)
            {
                list.Add(JsonElementToObject(element, elementType));
                return list;
            }

            foreach (var item in element.EnumerateArray())
            {
                list.Add(JsonElementToObject(item, elementType));
            }

            return list;
        }

        private object DeserializeDictionary(JsonElement element, Type dictType)
        {
            var valueType = dictType.GetGenericArguments()[1];
            var dict = (IDictionary)Activator.CreateInstance(dictType)!;

            if (element.ValueKind != JsonValueKind.Object)
                return dict;

            foreach (var prop in element.EnumerateObject())
            {
                dict[prop.Name] = JsonElementToObject(prop.Value, valueType);
            }

            return dict;
        }

        /// <summary>
        /// A struct from a JSON object that names each of its members, no more and no fewer. Anything else is an error, not
        /// a value: the deserializers read a member that is not there as 0, so "[1,2,3]", "#FF0000" or {"y":5} used to
        /// become the origin, black, and (0,5,0) — and set_property answered "set successfully".
        /// </summary>
        private object DeserializeStruct(JsonElement element, Type structType)
        {
            if (_typeRegistry.HasCustomDeserializer(structType))
                return DeserializeRegistered(element, structType);

            if (element.ValueKind != JsonValueKind.Object)
                throw new FormatException($"{structType.Name} is written as a JSON object {{{string.Join(", ", StructMemberNames(structType))}}}, not {Brief(element)}");
            return ConstructStruct(element, structType);
        }

        /// <summary>
        /// A struct with a registered deserializer. Its members are the keys its serializer writes for the default value, so
        /// the check follows the registration: a key it does not write is unknown; a key it writes and the JSON leaves out is
        /// missing — unless the deserializer fills it with a default of its own (a colour's alpha is 1), which shows as a
        /// different value when that member is given as zero.
        /// </summary>
        private object DeserializeRegistered(JsonElement element, Type type)
        {
            var wire = _typeRegistry.TrySerialize(Activator.CreateInstance(type)!) ?? new Dictionary<string, object?>();

            Dictionary<string, object?> given;
            if (element.ValueKind == JsonValueKind.Object)
                given = JsonElementToDictionary(element);
            else if (wire.Count == 1 && !(wire.Values.First() is Dictionary<string, object?>)
                     && element.ValueKind != JsonValueKind.Array)
                // A struct of one member takes the bare value too: a LayerMask as 256.
                given = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [wire.Keys.First()] = ScalarOf(element) };
            else
                throw new FormatException($"{type.Name} is written as a JSON object {Shape(wire)}, not {Brief(element)}");

            var problems = new List<string>();
            var missing = new List<string[]>();
            CompareShape(given, wire, Array.Empty<string>(), problems, missing);
            if (problems.Count > 0)
                throw new FormatException($"{type.Name} is written as {Shape(wire)}: {string.Join("; ", problems)}");

            var value = _typeRegistry.TryDeserialize(type, given)!;
            var zeroed = missing
                .Where(path => Equals(value, _typeRegistry.TryDeserialize(type, WithWireMember(given, wire, path))))
                .Select(path => string.Join(".", path))
                .ToList();
            if (zeroed.Count > 0)
                throw new FormatException($"{type.Name} is written as {Shape(wire)}; missing {string.Join(", ", zeroed)}");
            return value;
        }

        /// <summary>Keys the JSON gives that the wire form lacks, or gives as a value where the wire form nests an object.</summary>
        private static void CompareShape(Dictionary<string, object?> given, Dictionary<string, object?> wire, string[] path,
            List<string> problems, List<string[]> missing)
        {
            foreach (var entry in given)
            {
                var wireKey = wire.Keys.FirstOrDefault(k => string.Equals(k, entry.Key, StringComparison.OrdinalIgnoreCase));
                var where = string.Join(".", path.Concat(new[] { wireKey ?? entry.Key }));
                if (wireKey == null)
                    problems.Add($"no member {where}");
                else if (wire[wireKey] is Dictionary<string, object?> wireSub)
                {
                    if (entry.Value is Dictionary<string, object?> givenSub)
                        CompareShape(givenSub, wireSub, path.Concat(new[] { wireKey }).ToArray(), problems, missing);
                    else
                        problems.Add($"{where} is an object {Shape(wireSub)}");
                }
            }
            foreach (var wireKey in wire.Keys)
                if (!given.ContainsKey(wireKey))   // given is case-insensitive
                    missing.Add(path.Concat(new[] { wireKey }).ToArray());
        }

        /// <summary>A copy of <paramref name="given"/> with the member at <paramref name="path"/> set to its wire default.</summary>
        private static Dictionary<string, object?> WithWireMember(Dictionary<string, object?> given, Dictionary<string, object?> wire, string[] path)
        {
            var copy = new Dictionary<string, object?>(given, StringComparer.OrdinalIgnoreCase);
            var wireValue = wire[path[0]];
            if (path.Length == 1 || !(wireValue is Dictionary<string, object?> wireSub))
            {
                copy[path[0]] = wireValue;
                return copy;
            }
            var givenSub = given.TryGetValue(path[0], out var sub) && sub is Dictionary<string, object?> d
                ? d : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            copy[path[0]] = WithWireMember(givenSub, wireSub, path.Skip(1).ToArray());
            return copy;
        }

        private static string Shape(Dictionary<string, object?> wire) =>
            "{" + string.Join(", ", wire.Select(e => e.Value is Dictionary<string, object?> sub ? $"{e.Key}: {Shape(sub)}" : e.Key)) + "}";

        /// <summary>
        /// A struct through reflection: public fields, or — for a struct with none — its settable properties. Every one of
        /// them must be given; a key that names none of them is an error.
        /// </summary>
        private object ConstructStruct(JsonElement json, Type type)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var settable = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();
            var required = StructMemberNames(type);

            var boxed = Activator.CreateInstance(type)!;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unknown = new List<string>();
            foreach (var jsonProp in json.EnumerateObject())
            {
                var field = fields.FirstOrDefault(f => string.Equals(f.Name, jsonProp.Name, StringComparison.OrdinalIgnoreCase));
                if (field != null)
                {
                    field.SetValue(boxed, JsonElementToObject(jsonProp.Value, field.FieldType));
                    seen.Add(field.Name);
                    continue;
                }
                var prop = settable.FirstOrDefault(p => string.Equals(p.Name, jsonProp.Name, StringComparison.OrdinalIgnoreCase));
                if (prop != null)
                {
                    prop.SetValue(boxed, JsonElementToObject(jsonProp.Value, prop.PropertyType));
                    seen.Add(prop.Name);
                    continue;
                }
                unknown.Add(jsonProp.Name);
            }

            if (unknown.Count > 0)
                throw new FormatException($"{type.Name} is written as {{{string.Join(", ", required)}}}: no member {string.Join(", ", unknown)}");
            var missing = required.Where(name => !seen.Contains(name)).ToList();
            if (missing.Count > 0)
                throw new FormatException($"{type.Name} is written as {{{string.Join(", ", required)}}}; missing {string.Join(", ", missing)}");
            return boxed;
        }

        /// <summary>The members a struct is written with: its public fields, or its settable properties when it has none.</summary>
        private static List<string> StructMemberNames(Type type)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length > 0) return fields.Select(f => f.Name).ToList();
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .Select(p => p.Name).ToList();
        }

        private static object? ScalarOf(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };

        /// <summary>The JSON as it reads in an error message, cut short.</summary>
        private static string Brief(JsonElement element)
        {
            var text = element.GetRawText();
            return text.Length <= 60 ? text : text.Substring(0, 60) + "…";
        }

        private object DeserializeObjectViaReflection(JsonElement element, Type type)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return Activator.CreateInstance(type)!;

            return ConstructFromReflection(element, type);
        }

        private object ConstructFromReflection(JsonElement json, Type type)
        {
            var instance = Activator.CreateInstance(type);
            var boxed = (object)instance!;

            if (json.ValueKind != JsonValueKind.Object) return boxed;

            foreach (var jsonProp in json.EnumerateObject())
            {
                // Try field first (common for Unity structs like Vector3)
                var field = type.GetField(jsonProp.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field != null)
                {
                    var val = JsonElementToObject(jsonProp.Value, field.FieldType);
                    field.SetValue(boxed, val);
                    continue;
                }

                // Try property
                var prop = type.GetProperty(jsonProp.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null && prop.CanWrite)
                {
                    var val = JsonElementToObject(jsonProp.Value, prop.PropertyType);
                    prop.SetValue(boxed, val);
                }
            }

            return boxed;
        }

        #endregion

        #region Object → JSON

        public JsonElement ObjectToJsonElement(object? value)
        {
            if (value == null)
                return JsonSerializer.SerializeToElement<object?>(null);

            var type = value.GetType();

            // An enum is a value type too, and the struct branch below wrote its one public field: {"value__":1}.
            if (type.IsEnum)
                return JsonSerializer.SerializeToElement(value.ToString(), _jsonOptions);

            if (IsJsonScalar(type))
                return JsonSerializer.SerializeToElement(value, type, _jsonOptions);

            // Custom serializer?
            if (_typeRegistry.HasCustomSerializer(type))
            {
                var dict = _typeRegistry.TrySerialize(value);
                return JsonSerializer.SerializeToElement(dict, _jsonOptions);
            }

            // Array
            if (type.IsArray)
                return SerializeArray((Array)value);

            // List<T>
            if (value is IList list && IsGenericList(type))
                return SerializeList(list);

            // Dictionary
            if (value is IDictionary dict2 && IsStringDictionary(type))
                return SerializeDictionary(dict2);

            // Struct — serialize fields to JSON object
            if (type.IsValueType && !type.IsPrimitive && type != typeof(decimal))
                return SerializeStruct(value, type);

            // Default: let System.Text.Json handle it
            return JsonSerializer.SerializeToElement(value, type, _jsonOptions);
        }

        private JsonElement SerializeArray(Array array)
        {
            var list = new List<object?>();
            var elementType = array.GetType().GetElementType()!;

            for (int i = 0; i < array.Length; i++)
            {
                var item = array.GetValue(i);
                list.Add(SerializeValue(item));
            }

            return JsonSerializer.SerializeToElement(list, _jsonOptions);
        }

        private JsonElement SerializeList(IList list)
        {
            var result = new List<object?>();
            foreach (var item in list)
                result.Add(SerializeValue(item));
            return JsonSerializer.SerializeToElement(result, _jsonOptions);
        }

        private JsonElement SerializeDictionary(IDictionary dict)
        {
            var result = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in dict)
                result[entry.Key.ToString()!] = SerializeValue(entry.Value);
            return JsonSerializer.SerializeToElement(result, _jsonOptions);
        }

        private JsonElement SerializeStruct(object value, Type type)
            => JsonSerializer.SerializeToElement(StructMembers(value, type, 0), _jsonOptions);

        /// <summary>
        /// How deep nested values are followed before one is written as its ToString(). Only a cycle the property
        /// guard in <see cref="StructMembers"/> cannot see gets anywhere near it (A.b returns a B whose .a returns an A).
        /// </summary>
        private const int MaxValueDepth = 16;

        /// <summary>
        /// A struct as its public fields, or — when it has none — its readable properties. A property of the struct's
        /// OWN type is skipped: it is a derived view (DateTime.Date, Vector3.normalized), and following it never ends —
        /// the fallback used to recurse until the stack overflowed, which takes the editor down with it.
        /// </summary>
        private Dictionary<string, object?> StructMembers(object value, Type type, int depth)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                dict[field.Name] = SerializeValue(field.GetValue(value), depth + 1);
            if (dict.Count > 0) return dict;

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || prop.PropertyType == type) continue;
                try { dict[prop.Name] = SerializeValue(prop.GetValue(value), depth + 1); } catch { }
            }
            return dict;
        }

        private object? SerializeValue(object? value, int depth = 0)
        {
            if (value == null) return null;
            var type = value.GetType();

            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || IsJsonScalar(type))
                return value;

            if (type.IsEnum)
                return value.ToString();

            if (depth > MaxValueDepth)
                return value.ToString();

            if (_typeRegistry.HasCustomSerializer(type))
                return _typeRegistry.TrySerialize(value);

            if (type.IsValueType)
                return StructMembers(value, type, depth);

            if (type.IsArray)
            {
                var arr = (Array)value;
                var list = new List<object?>();
                for (int i = 0; i < arr.Length; i++)
                    list.Add(SerializeValue(arr.GetValue(i), depth + 1));
                return list;
            }

            if (value is IList listVal)
            {
                var list = new List<object?>();
                foreach (var item in listVal)
                    list.Add(SerializeValue(item, depth + 1));
                return list;
            }

            return value.ToString();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// A JSON object as the dictionary a registered deserializer reads. A nested object is a nested dictionary — the
        /// shape the registered serializers write (Bounds is {center:{x,y,z}, size:{x,y,z}}) — not its raw text: handed
        /// text, the nested reads found no dictionary and every Bounds, BoundsInt and Aabb came out zero.
        /// </summary>
        private static Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (element.ValueKind != JsonValueKind.Object) return dict;

            foreach (var prop in element.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    JsonValueKind.Object => JsonElementToDictionary(prop.Value),
                    _ => prop.Value.GetRawText()
                };
            }
            return dict;
        }

        /// <summary>
        /// Value types that System.Text.Json writes as one JSON value. The struct branch took them apart: a Guid has no
        /// public fields, so it came out as {} (or {"variant":…,"version":…} on .NET 9) and the id was lost.
        /// </summary>
        private static bool IsJsonScalar(Type type) =>
            type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan);

        private static bool IsGenericList(Type type)
        {
            return type.IsGenericType &&
                (type.GetGenericTypeDefinition() == typeof(List<>) ||
                 type.GetGenericTypeDefinition() == typeof(IList<>));
        }

        private static bool IsStringDictionary(Type type)
        {
            if (!type.IsGenericType) return false;
            var genDef = type.GetGenericTypeDefinition();
            return (genDef == typeof(Dictionary<,>) || genDef == typeof(IDictionary<,>))
                && type.GetGenericArguments()[0] == typeof(string);
        }

        #endregion
    }
}
