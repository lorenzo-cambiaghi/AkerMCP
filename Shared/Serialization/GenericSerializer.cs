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

        private object? DeserializeEnum(JsonElement element, Type enumType)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var str = element.GetString();
                if (str != null && Enum.TryParse(enumType, str, true, out var val))
                    return val;
            }
            if (element.ValueKind == JsonValueKind.Number)
                return Enum.ToObject(enumType, element.GetInt32());
            return Activator.CreateInstance(enumType);
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

        private object DeserializeStruct(JsonElement element, Type structType)
        {
            // Custom deserializer registered?
            if (_typeRegistry.HasCustomDeserializer(structType))
            {
                var dict = JsonElementToDictionary(element);
                var result = _typeRegistry.TryDeserialize(structType, dict);
                if (result != null) return result;
            }

            // Fallback: reflection on fields + properties
            return ConstructFromReflection(element, structType);
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
        {
            var dict = new Dictionary<string, object?>();

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var fieldValue = field.GetValue(value);
                dict[field.Name] = SerializeValue(fieldValue);
            }

            // If no public fields (property-based struct), use properties
            if (dict.Count == 0)
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    try
                    {
                        var propValue = prop.GetValue(value);
                        dict[prop.Name] = SerializeValue(propValue);
                    }
                    catch { }
                }
            }

            return JsonSerializer.SerializeToElement(dict, _jsonOptions);
        }

        private object? SerializeValue(object? value)
        {
            if (value == null) return null;
            var type = value.GetType();

            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return value;

            if (type.IsEnum)
                return value.ToString();

            if (_typeRegistry.HasCustomSerializer(type))
                return _typeRegistry.TrySerialize(value);

            if (type.IsValueType && !type.IsPrimitive)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    dict[field.Name] = SerializeValue(field.GetValue(value));
                if (dict.Count == 0)
                {
                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                        try { dict[prop.Name] = SerializeValue(prop.GetValue(value)); } catch { }
                    }
                }
                return dict;
            }

            if (type.IsArray)
            {
                var arr = (Array)value;
                var list = new List<object?>();
                for (int i = 0; i < arr.Length; i++)
                    list.Add(SerializeValue(arr.GetValue(i)));
                return list;
            }

            if (value is IList listVal)
            {
                var list = new List<object?>();
                foreach (var item in listVal)
                    list.Add(SerializeValue(item));
                return list;
            }

            return value.ToString();
        }

        #endregion

        #region Helpers

        private Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
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
                    _ => prop.Value.GetRawText()
                };
            }
            return dict;
        }

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
