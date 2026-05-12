using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AkerMcp.Shared.Reflection
{
    public class MethodInvoker
    {
        public object? Invoke(object target, string methodName, object?[]? args)
        {
            var type = target.GetType();
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (candidates.Length == 0)
                throw new PropertyPathException($"Method '{methodName}' not found on type {type.Name}");

            var argCount = args?.Length ?? 0;
            var method = FindBestOverload(candidates, args);

            if (method == null)
                throw new PropertyPathException(
                    $"No matching overload for '{methodName}' with {argCount} argument(s) on type {type.Name}");

            var parameters = method.GetParameters();
            var convertedArgs = ConvertArguments(parameters, args);

            return method.Invoke(target, convertedArgs);
        }

        public object? InvokeStatic(Type type, string methodName, object?[]? args)
        {
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (candidates.Length == 0)
                throw new PropertyPathException($"Static method '{methodName}' not found on type {type.Name}");

            var method = FindBestOverload(candidates, args);

            if (method == null)
                throw new PropertyPathException(
                    $"No matching overload for static '{methodName}' with {args?.Length ?? 0} argument(s)");

            var parameters = method.GetParameters();
            var convertedArgs = ConvertArguments(parameters, args);

            return method.Invoke(null, convertedArgs);
        }

        private MethodInfo? FindBestOverload(MethodInfo[] candidates, object?[]? args)
        {
            var argCount = args?.Length ?? 0;

            var exact = candidates.Where(m =>
            {
                var parms = m.GetParameters();
                return parms.Length == argCount;
            }).ToArray();

            if (exact.Length == 1) return exact[0];

            var withOptional = candidates.Where(m =>
            {
                var parms = m.GetParameters();
                var required = parms.Count(p => !p.IsOptional);
                return argCount >= required && argCount <= parms.Length;
            }).ToArray();

            if (withOptional.Length >= 1) return withOptional[0];

            if (argCount == 0)
            {
                var parameterless = candidates.FirstOrDefault(m => m.GetParameters().Length == 0);
                if (parameterless != null) return parameterless;
            }

            return candidates.FirstOrDefault();
        }

        private object?[] ConvertArguments(ParameterInfo[] parameters, object?[]? args)
        {
            var result = new object?[parameters.Length];
            var argCount = args?.Length ?? 0;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (i < argCount && args != null)
                {
                    result[i] = ConvertArgument(args[i], parameters[i].ParameterType);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    result[i] = parameters[i].DefaultValue;
                }
                else
                {
                    result[i] = parameters[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(parameters[i].ParameterType)
                        : null;
                }
            }

            return result;
        }

        private static object? ConvertArgument(object? value, Type targetType)
        {
            if (value == null) return null;

            var valueType = value.GetType();
            if (targetType.IsAssignableFrom(valueType)) return value;

            try
            {
                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                throw new PropertyPathException(
                    $"Cannot convert argument of type {valueType.Name} to {targetType.Name}");
            }
        }
    }
}
