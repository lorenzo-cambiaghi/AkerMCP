using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AkerMcp.Shared.Reflection;
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
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                // Engines hold infinities on purpose (a far plane, a "no limit" velocity, a default argument of
                // -Infinity): without this, one of them anywhere in a value threw and the whole reply was lost.
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
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
            if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            {
                // A null where no null fits is a value that does not fit: a struct member given as null read as 0.
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    throw new FormatException($"null is not a {targetType.Name}");
                return null;
            }

            // Nullable<T> — unwrap
            var underlyingNullable = Nullable.GetUnderlyingType(targetType);
            if (underlyingNullable != null)
                return JsonElementToObject(element, underlyingNullable);

            // Primitives. Every one is read whole or refused: the casts these replace wrapped and truncated without a
            // word (3000000000 into an int was -2147483648, 300 into a byte 44, 1.5 into an int 1, ulong.MaxValue 0),
            // and text was parsed with NumberStyles.Any, where "0,5" is 5 because the comma is a thousands separator.
            if (targetType == typeof(string)) return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            if (targetType == typeof(object)) return PlainValue(element);
            if (targetType == typeof(bool)) return ReadBool(element);
            if (targetType == typeof(char)) return ReadChar(element);
            if (Array.IndexOf(IntegerTypes, targetType) >= 0) return ReadInteger(element, targetType);
            if (targetType == typeof(float)) return ReadFloat(element);
            if (targetType == typeof(double)) return ReadDouble(element);
            if (targetType == typeof(decimal)) return ReadDecimal(element);

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
            if (targetType.IsValueType)
                return DeserializeStruct(element, targetType);

            return DeserializeClass(element, targetType);
        }

        private static readonly Type[] IntegerTypes =
        {
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong)
        };

        /// <summary>A whole number that fits <paramref name="type"/>, from a JSON number or a numeric string, or an error.</summary>
        private static object ReadInteger(JsonElement element, Type type)
        {
            decimal number;
            if (element.ValueKind == JsonValueKind.Number)
            {
                if (!element.TryGetDecimal(out number))
                    throw new FormatException($"{Brief(element)} does not fit in {type.Name} ({Range(type)})");
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString() ?? "";
                if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw new FormatException(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        ? $"{Brief(element)} does not fit in {type.Name} ({Range(type)})"
                        : $"{Brief(element)} is not a {type.Name}");
            }
            else
            {
                throw new FormatException($"{Brief(element)} is not a {type.Name}");
            }

            if (number != decimal.Truncate(number))
                throw new FormatException($"{Brief(element)} is not a whole number, and {type.Name} holds only whole numbers");
            try
            {
                return Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                throw new FormatException($"{Brief(element)} does not fit in {type.Name} ({Range(type)})");
            }
        }

        private static string Range(Type type) =>
            string.Format(CultureInfo.InvariantCulture, "{0} to {1}",
                type.GetField("MinValue")?.GetValue(null), type.GetField("MaxValue")?.GetValue(null));

        private static double ReadDouble(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetDouble(out var number)) return number;
                throw new FormatException($"{Brief(element)} does not fit in Double");
            }
            if (element.ValueKind == JsonValueKind.String)
            {
                if (double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                throw new FormatException($"{Brief(element)} is not a number (the decimal separator is '.')");
            }
            throw new FormatException($"{Brief(element)} is not a number");
        }

        private static object ReadFloat(JsonElement element)
        {
            var number = ReadDouble(element);
            var single = (float)number;
            if (float.IsInfinity(single) && !double.IsInfinity(number))
                throw new FormatException($"{Brief(element)} does not fit in Single (±3.4e38)");
            return single;
        }

        private static object ReadDecimal(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number)) return number;
            if (element.ValueKind == JsonValueKind.String
                && decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            throw new FormatException($"{Brief(element)} is not a Decimal");
        }

        private static object ReadBool(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.True) return true;
            if (element.ValueKind == JsonValueKind.False) return false;
            if (element.ValueKind == JsonValueKind.String && bool.TryParse((element.GetString() ?? "").Trim(), out var parsed)) return parsed;
            throw new FormatException($"{Brief(element)} is not true or false");
        }

        private static object ReadChar(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: 1 } text) return text[0];
            throw new FormatException($"{Brief(element)} is not a single character");
        }

        /// <summary>
        /// A JSON value as the plain value it reads as, for a member or parameter typed <c>object</c>. System.Text.Json
        /// answers with the JsonElement itself — a type of this bridge's JSON library, stored in a game's field or handed
        /// to an engine API that has never heard of it.
        /// </summary>
        private static object? PlainValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number: return element.TryGetInt64(out var whole) ? (object)whole : element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object?>();
                    foreach (var member in element.EnumerateObject()) dict[member.Name] = PlainValue(member.Value);
                    return dict;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(PlainValue).ToList();
                default:
                    return null;
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
            // A member declared as IList<T> or IReadOnlyList<T> gets a List<T>: Activator cannot build an interface, and
            // threw MissingMethodException for every one of them.
            var list = (IList)Activator.CreateInstance(listType.IsInterface ? typeof(List<>).MakeGenericType(elementType) : listType)!;

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
            var dict = (IDictionary)Activator.CreateInstance(dictType.IsInterface
                ? typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType)
                : dictType)!;

            // Anything but an object used to come back as an EMPTY dictionary, and the member was cleared.
            if (element.ValueKind != JsonValueKind.Object)
                throw new FormatException($"{dictType.Name} is written as a JSON object of its entries, not {Brief(element)}");

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
            {
                if (TryFromScalar(element, structType, out var converted)) return converted!;
                throw new FormatException($"{structType.Name} is written as a JSON object {{{string.Join(", ", StructMemberNames(structType))}}}, not {Brief(element)}");
            }
            return ConstructStruct(element, structType);
        }

        /// <summary>
        /// A class. A string, number or boolean goes through the type's own conversion (<see cref="TryFromScalar"/>) or is
        /// refused; an object or an array goes to System.Text.Json, then to reflection for an object. Every one of these
        /// used to end, on failure, in Activator.CreateInstance: Godot's <c>AudioStreamPlayer.Bus = "SFX"</c> became an
        /// EMPTY StringName and set_property answered success, and an array for a class became a blank instance.
        /// </summary>
        private object? DeserializeClass(JsonElement element, Type type)
        {
            // A scene object, an asset or a node lives in the engine, not in a JSON value: built from one (System.Text.Json
            // runs new GameObject(), new Node()), it would be a stray object in the scene or the node tree.
            if (_typeRegistry.IsReferenceOnly(type))
                throw new FormatException(
                    $"{type.Name} is an engine object and cannot be built from {Brief(element)}: pass null to clear the reference, or assign an existing one with execute");

            if (element.ValueKind != JsonValueKind.Object && element.ValueKind != JsonValueKind.Array)
            {
                if (TryFromScalar(element, type, out var converted)) return converted;
                throw new FormatException($"{type.Name} cannot be made from {Brief(element)}: it has no conversion from a {KindName(element)}");
            }

            if (element.ValueKind == JsonValueKind.Object && _typeRegistry.HasCustomDeserializer(type))
                return _typeRegistry.TryDeserialize(type, JsonElementToDictionary(element));

            try
            {
                return JsonSerializer.Deserialize(element.GetRawText(), type, _jsonOptions);
            }
            catch (Exception jsonError) when (element.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    return ConstructFromReflection(element, type);
                }
                catch (Exception ex)
                {
                    throw new FormatException($"{type.Name} cannot be built from {Brief(element)}: {(ex is MissingMethodException ? ex.Message : jsonError.Message)}", ex);
                }
            }
            catch (Exception ex)
            {
                throw new FormatException($"{type.Name} cannot be built from {Brief(element)}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// A JSON string, number or boolean through the type's own conversion from it: an implicit operator (Godot's
        /// StringName and NodePath from a string, Variant from any of the three), or — for a string — a constructor taking
        /// one (Uri, Version). A disposable type is never constructed from text: its constructor opens what it wraps, and
        /// <c>new StreamWriter(path)</c> creates the file. Overload resolution tries this on every candidate.
        /// </summary>
        private static bool TryFromScalar(JsonElement element, Type type, out object? result)
        {
            result = null;
            Type[] sources;
            switch (element.ValueKind)
            {
                case JsonValueKind.String: sources = new[] { typeof(string) }; break;
                case JsonValueKind.Number:
                    sources = element.TryGetInt64(out _)
                        ? new[] { typeof(long), typeof(int), typeof(double), typeof(float) }
                        : new[] { typeof(double), typeof(float) };
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False: sources = new[] { typeof(bool) }; break;
                default: return false;
            }

            foreach (var source in sources)
            {
                var conversion = type.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m =>
                    m.Name == "op_Implicit" && m.ReturnType == type
                    && m.GetParameters() is { Length: 1 } p && p[0].ParameterType == source);
                if (conversion == null) continue;

                object? argument;
                if (source == typeof(string)) argument = element.GetString();
                else if (source == typeof(long)) argument = element.GetInt64();
                else if (source == typeof(int)) { if (!element.TryGetInt32(out var small)) continue; argument = small; }
                else if (source == typeof(double)) argument = element.GetDouble();
                else if (source == typeof(float)) argument = (float)element.GetDouble();
                else argument = element.GetBoolean();

                result = InvokeUnwrapped(() => conversion.Invoke(null, new[] { argument }));
                return true;
            }

            if (element.ValueKind == JsonValueKind.String && !type.IsAbstract && !typeof(IDisposable).IsAssignableFrom(type))
            {
                var constructor = type.GetConstructor(new[] { typeof(string) });
                if (constructor != null)
                {
                    result = InvokeUnwrapped(() => constructor.Invoke(new object?[] { element.GetString() }));
                    return true;
                }
            }
            return false;
        }

        /// <summary>The engine's own exception, not the TargetInvocationException reflection wraps it in.</summary>
        private static object? InvokeUnwrapped(Func<object?> call)
        {
            try { return call(); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static string KindName(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True => "boolean",
            JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => element.ValueKind.ToString().ToLowerInvariant()
        };

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

        private object ConstructFromReflection(JsonElement json, Type type)
        {
            var boxed = Activator.CreateInstance(type)!;

            foreach (var jsonProp in json.EnumerateObject())
            {
                // Through the cache's lookup, which picks one member where GetField/GetProperty with IgnoreCase threw
                // AmbiguousMatchException (two names differing only in case, a property re-declared with `new`).
                var field = ReflectionCache.FindField(type, jsonProp.Name);
                if (field != null)
                {
                    field.SetValue(boxed, JsonElementToObject(jsonProp.Value, field.FieldType));
                    continue;
                }

                var prop = ReflectionCache.FindProperty(type, jsonProp.Name);
                if (prop != null && prop.CanWrite)
                    InvokeUnwrapped(() => { prop.SetValue(boxed, JsonElementToObject(jsonProp.Value, prop.PropertyType)); return null; });
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

            if (IsJsonScalar(type) || type.IsPrimitive || value is string || value is decimal)
                return JsonSerializer.SerializeToElement(value, type, _jsonOptions);

            // Custom serializer?
            if (_typeRegistry.HasCustomSerializer(type))
            {
                var dict = _typeRegistry.TrySerialize(value);
                return JsonSerializer.SerializeToElement(dict, _jsonOptions);
            }

            // Arrays of any rank, lists, dictionaries of any key, structs: the bounded walk of SerializeValue.
            if (value is IList || value is IDictionary || type.IsValueType)
                return JsonSerializer.SerializeToElement(SerializeValue(value), _jsonOptions);

            // A class. System.Text.Json follows every property, and a class holding a struct with a view of itself (a
            // Unity component holds Vector3s, and Vector3.normalized is a Vector3) runs it 64 levels down until it
            // throws: get_property of a Transform, or call_method returning a component, failed outright. When it cannot
            // write the object, the object is written one level deep instead. Engine objects skip it altogether: it runs
            // every getter, including the ones that make a copy in the scene.
            if (!_typeRegistry.IsReferenceOnly(type))
            {
                try { return JsonSerializer.SerializeToElement(value, type, _jsonOptions); }
                catch (Exception) { /* written below, one level deep */ }
            }
            return JsonSerializer.SerializeToElement(ObjectMembers(value, type), _jsonOptions);
        }

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

        /// <summary>
        /// A class as its public fields and readable properties, named as System.Text.Json names them, every nested object
        /// written as its text — as it is inside an array or a struct. Left out: indexers, obsolete members (Unity's
        /// Component.rigidbody and its siblings throw by design), members that throw, and members whose getter makes a copy
        /// in the scene (<see cref="SharedMembers"/>: Renderer.material instantiates a material).
        /// </summary>
        private Dictionary<string, object?> ObjectMembers(object value, Type type)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsDefined(typeof(ObsoleteAttribute), true)) continue;
                try { dict[JsonNamingPolicy.CamelCase.ConvertName(field.Name)] = SerializeValue(field.GetValue(value), 1); }
                catch { /* a member that cannot be read is left out */ }
            }
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || prop.IsDefined(typeof(ObsoleteAttribute), true)) continue;
                var name = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
                if (dict.ContainsKey(name) || SharedMembers.SharedTwin(type, prop.Name) != null) continue;
                try { dict[name] = SerializeValue(prop.GetValue(value), 1); }
                catch { /* a member that cannot be read is left out */ }
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

            // GetValue(i) takes one dimension only: a float[,] threw "Array was not a one-dimensional array". A
            // multi-dimensional array is written as nested lists, one level per dimension.
            if (value is Array array && (array.Rank > 1 || array.GetLowerBound(0) != 0))
                return Dimension(array, new int[array.Rank], 0, depth);

            // A dictionary inside a list or another dictionary used to be written as its type name.
            if (value is IDictionary dict)
            {
                var entries = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dict)
                    entries[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = SerializeValue(entry.Value, depth + 1);
                return entries;
            }

            if (value is IList items)
            {
                var list = new List<object?>(items.Count);
                foreach (var item in items)
                    list.Add(SerializeValue(item, depth + 1));
                return list;
            }

            return value.ToString();
        }

        private List<object?> Dimension(Array array, int[] index, int dimension, int depth)
        {
            var list = new List<object?>();
            for (int i = array.GetLowerBound(dimension); i <= array.GetUpperBound(dimension); i++)
            {
                index[dimension] = i;
                list.Add(dimension == array.Rank - 1
                    ? SerializeValue(array.GetValue(index), depth + 1)
                    : Dimension(array, index, dimension + 1, depth + 1));
            }
            return list;
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
            if (!type.IsGenericType) return false;
            var genDef = type.GetGenericTypeDefinition();
            return genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(ICollection<>)
                || genDef == typeof(IEnumerable<>) || genDef == typeof(IReadOnlyList<>) || genDef == typeof(IReadOnlyCollection<>);
        }

        private static bool IsStringDictionary(Type type)
        {
            if (!type.IsGenericType) return false;
            var genDef = type.GetGenericTypeDefinition();
            return (genDef == typeof(Dictionary<,>) || genDef == typeof(IDictionary<,>) || genDef == typeof(IReadOnlyDictionary<,>))
                && type.GetGenericArguments()[0] == typeof(string);
        }

        #endregion
    }
}
