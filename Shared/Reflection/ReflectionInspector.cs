using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MCPSharp.Shared.Abstraction;

namespace MCPSharp.Shared.Reflection
{
    public class ReflectionInspector
    {
        private static readonly HashSet<string> SkipProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Item" // indexers
        };

        private static readonly HashSet<string> SkipMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GetType", "ToString", "Equals", "GetHashCode", "MemberwiseClone", "Finalize"
        };

        public InspectionResult Inspect(object target, int depth = 1,
            bool includeMethods = false, string? filter = null)
        {
            var type = target.GetType();
            var result = new InspectionResult
            {
                TypeName = type.FullName ?? type.Name,
                Properties = GetProperties(target, depth, filter),
                Methods = includeMethods ? GetMethods(type, filter) : null
            };
            return result;
        }

        public InspectionResult InspectType(Type type, bool includeMethods = false, string? filter = null)
        {
            var result = new InspectionResult
            {
                TypeName = type.FullName ?? type.Name,
                Properties = GetPropertyDescriptors(type, filter),
                Methods = includeMethods ? GetMethods(type, filter) : null
            };
            return result;
        }

        private List<PropertyDescriptor> GetProperties(object obj, int depth, string? filter)
        {
            var props = new List<PropertyDescriptor>();
            var type = obj.GetType();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (SkipProperties.Contains(prop.Name)) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                if (filter != null && !Regex.IsMatch(prop.Name, filter, RegexOptions.IgnoreCase)) continue;

                var descriptor = new PropertyDescriptor
                {
                    Name = prop.Name,
                    TypeName = FormatTypeName(prop.PropertyType),
                    CanRead = prop.CanRead,
                    CanWrite = prop.CanWrite,
                    Value = depth > 0 && prop.CanRead ? SafeGetValue(obj, prop, depth - 1) : null
                };
                props.Add(descriptor);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (filter != null && !Regex.IsMatch(field.Name, filter, RegexOptions.IgnoreCase)) continue;

                var descriptor = new PropertyDescriptor
                {
                    Name = field.Name,
                    TypeName = FormatTypeName(field.FieldType),
                    CanRead = true,
                    CanWrite = !field.IsInitOnly,
                    Value = depth > 0 ? SafeGetFieldValue(obj, field, depth - 1) : null
                };
                props.Add(descriptor);
            }

            return props;
        }

        private List<PropertyDescriptor> GetPropertyDescriptors(Type type, string? filter)
        {
            var props = new List<PropertyDescriptor>();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (SkipProperties.Contains(prop.Name)) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                if (filter != null && !Regex.IsMatch(prop.Name, filter, RegexOptions.IgnoreCase)) continue;

                props.Add(new PropertyDescriptor
                {
                    Name = prop.Name,
                    TypeName = FormatTypeName(prop.PropertyType),
                    CanRead = prop.CanRead,
                    CanWrite = prop.CanWrite
                });
            }

            return props;
        }

        private List<MethodDescriptor> GetMethods(Type type, string? filter)
        {
            var methods = new List<MethodDescriptor>();

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.IsSpecialName) continue;
                if (SkipMethods.Contains(method.Name)) continue;
                if (filter != null && !Regex.IsMatch(method.Name, filter, RegexOptions.IgnoreCase)) continue;

                var descriptor = new MethodDescriptor
                {
                    Name = method.Name,
                    ReturnType = FormatTypeName(method.ReturnType),
                    Parameters = method.GetParameters().Select(p => new ParameterDescriptor
                    {
                        Name = p.Name ?? "arg",
                        TypeName = FormatTypeName(p.ParameterType),
                        IsOptional = p.IsOptional,
                        DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
                    }).ToList()
                };
                methods.Add(descriptor);
            }

            return methods;
        }

        private object? SafeGetValue(object obj, PropertyInfo prop, int remainingDepth)
        {
            try
            {
                var value = prop.GetValue(obj);
                if (value == null) return null;
                if (IsSimpleType(prop.PropertyType)) return value;
                if (remainingDepth <= 0) return $"[{FormatTypeName(prop.PropertyType)}]";
                return value.ToString();
            }
            catch
            {
                return "[error reading value]";
            }
        }

        private object? SafeGetFieldValue(object obj, FieldInfo field, int remainingDepth)
        {
            try
            {
                var value = field.GetValue(obj);
                if (value == null) return null;
                if (IsSimpleType(field.FieldType)) return value;
                if (remainingDepth <= 0) return $"[{FormatTypeName(field.FieldType)}]";
                return value.ToString();
            }
            catch
            {
                return "[error reading value]";
            }
        }

        private static bool IsSimpleType(Type type)
        {
            return type.IsPrimitive
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(Guid)
                || type.IsEnum
                || (Nullable.GetUnderlyingType(type)?.IsPrimitive ?? false);
        }

        private static string FormatTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(long)) return "long";

            if (type.IsGenericType)
            {
                var baseName = type.Name.Split('`')[0];
                var args = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
                return $"{baseName}<{args}>";
            }

            return type.Name;
        }
    }
}
