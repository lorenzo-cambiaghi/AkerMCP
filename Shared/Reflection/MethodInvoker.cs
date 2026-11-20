using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AkerMcp.Shared.Serialization;

namespace AkerMcp.Shared.Reflection
{
    /// <summary>
    /// Calls a method by name with the arguments as the agent wrote them: JSON values (call_method hands each one over as it
    /// arrived), strings, or values already of the parameter's type. The overload is the one the arguments fit, converted
    /// per parameter through the same serializer set_property uses — a vector from {"x":..}, an enum by name, a StringName
    /// from a string, numbers in the invariant culture.
    /// </summary>
    public class MethodInvoker
    {
        // How well an argument fits a parameter, best first. Only applicable overloads are ranked, by arity, then fit.
        private const int Natural = 4;   // "walk" for a string, 3 for an int, {"x":..} for a struct
        private const int Widened = 3;   // 3 for a float, 1 for an enum, null for a reference
        private const int Converted = 2; // "SFX" through StringName's implicit operator, anything for an object
        private const int Lenient = 1;   // "0.5" for a float, "true" for a bool, 5 for a string

        private static readonly JsonSerializerOptions ScalarOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private readonly GenericSerializer _serializer;

        public MethodInvoker() : this(new GenericSerializer()) { }

        public MethodInvoker(GenericSerializer serializer)
        {
            _serializer = serializer;
        }

        public object? Invoke(object target, string methodName, object?[]? args)
            => Invoke(new[] { target }, methodName, args);

        /// <summary>
        /// The method on the first of <paramref name="targets"/> that has an overload the arguments fit: a Unity node passes
        /// its components and then the GameObject, a Stride node its components and then the entity. The nodes used to
        /// pick the target with GetMethod(name, IgnoreCase), which throws AmbiguousMatchException on any overloaded name —
        /// Rigidbody.AddForce, Transform.Translate, Animator.Play, Godot's Node.GetNode — so none of them could be called.
        /// </summary>
        public object? Invoke(IEnumerable<object> targets, string methodName, object?[]? args)
        {
            var searched = new List<Type>();
            var refusals = new List<string>();
            foreach (var target in targets)
            {
                if (target == null) continue;
                var type = target.GetType();
                searched.Add(type);
                var candidates = Candidates(type, methodName, BindingFlags.Instance);
                if (candidates.Count == 0) continue;
                var call = Choose(candidates, args, refusals);
                if (call != null) return Run(call.Method, target, call.Arguments);
            }
            throw NoFit(methodName, args, searched, refusals);
        }

        public object? InvokeStatic(Type type, string methodName, object?[]? args)
        {
            var refusals = new List<string>();
            var candidates = Candidates(type, methodName, BindingFlags.Static);
            if (candidates.Count > 0)
            {
                var call = Choose(candidates, args, refusals);
                if (call != null) return Run(call.Method, null, call.Arguments);
            }
            throw NoFit(methodName, args, new[] { type }, refusals, "static ");
        }

        // A generic method definition (GetNode<T>) has no argument that says what T is, and Invoke would throw on it.
        private static List<MethodInfo> Candidates(Type type, string name, BindingFlags scope) =>
            type.GetMethods(BindingFlags.Public | scope)
                .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                            && !m.IsGenericMethodDefinition
                            && !m.GetParameters().Any(p => p.ParameterType.IsPointer))
                .ToList();

        private sealed class Binding
        {
            public MethodInfo Method = null!;
            public object?[] Arguments = Array.Empty<object?>();
            public bool ExactArity;
            public int Fit;
            public int Defaulted;
            public int Order;
        }

        private Binding? Choose(List<MethodInfo> candidates, object?[]? args, List<string> refusals)
        {
            var given = args ?? Array.Empty<object?>();
            Binding? best = null;
            for (int order = 0; order < candidates.Count; order++)
            {
                var binding = Bind(candidates[order], given, out var reason);
                if (binding == null)
                {
                    refusals.Add($"{Signature(candidates[order])}: {reason}");
                    continue;
                }
                binding.Order = order;
                if (best == null || Better(binding, best)) best = binding;
            }
            return best;
        }

        // Exact arity, then how well the arguments fit, then fewer defaults filled in, then the most derived declaration
        // (a `new` method hides its base), then declaration order.
        private static bool Better(Binding a, Binding b)
        {
            if (a.ExactArity != b.ExactArity) return a.ExactArity;
            if (a.Fit != b.Fit) return a.Fit > b.Fit;
            if (a.Defaulted != b.Defaulted) return a.Defaulted < b.Defaulted;
            var aType = a.Method.DeclaringType;
            var bType = b.Method.DeclaringType;
            if (aType != null && bType != null && aType != bType) return aType.IsSubclassOf(bType);
            return a.Order < b.Order;
        }

        /// <summary>
        /// The arguments for <paramref name="method"/>, or null with the reason it does not take them. A missing argument is
        /// filled only from a declared default, never invented — SetActive() with no argument used to call SetActive(false),
        /// any required parameter being given its type's zero — and an extra one is never dropped.
        /// </summary>
        private Binding? Bind(MethodInfo method, object?[] given, out string reason)
        {
            var parameters = method.GetParameters();
            int k = parameters.Length, n = given.Length;
            bool hasParamsArray = k > 0 && parameters[k - 1].ParameterType.IsArray
                && parameters[k - 1].IsDefined(typeof(ParamArrayAttribute), false);

            var normal = BindNormal(method, parameters, given, out reason);
            if (normal != null || !hasParamsArray) return normal;

            // params T[]: the trailing arguments become the array, as in C#'s expanded form.
            int fixedCount = k - 1;
            if (n < fixedCount)
            {
                reason = $"needs at least {fixedCount} argument(s), {n} given";
                return null;
            }
            var arguments = new object?[k];
            int fit = 0;
            for (int i = 0; i < fixedCount; i++)
            {
                if (!TryConvert(given[i], parameters[i].ParameterType, out arguments[i], out var f, out var why))
                {
                    reason = $"argument {i + 1} ({parameters[i].Name}): {why}";
                    return null;
                }
                fit += f;
            }
            var elementType = parameters[k - 1].ParameterType.GetElementType()!;
            var rest = Array.CreateInstance(elementType, n - fixedCount);
            for (int j = 0; j < rest.Length; j++)
            {
                if (!TryConvert(given[fixedCount + j], elementType, out var item, out var f, out var why))
                {
                    reason = $"argument {fixedCount + j + 1} ({parameters[k - 1].Name}[{j}]): {why}";
                    return null;
                }
                rest.SetValue(item, j);
                fit += f;
            }
            arguments[k - 1] = rest;
            reason = "";
            return new Binding { Method = method, Arguments = arguments, ExactArity = false, Fit = fit, Defaulted = 0 };
        }

        private Binding? BindNormal(MethodInfo method, ParameterInfo[] parameters, object?[] given, out string reason)
        {
            int k = parameters.Length, n = given.Length;
            if (n > k)
            {
                reason = $"takes {Arity(parameters)} argument(s), {n} given";
                return null;
            }

            var arguments = new object?[k];
            int fit = 0, defaulted = 0;
            for (int i = 0; i < k; i++)
            {
                var parameter = parameters[i];
                if (i < n)
                {
                    if (!TryConvert(given[i], parameter.ParameterType, out arguments[i], out var f, out var why))
                    {
                        reason = $"argument {i + 1} ({parameter.Name}): {why}";
                        return null;
                    }
                    fit += f;
                }
                else if (parameter.HasDefaultValue)
                {
                    arguments[i] = DefaultOf(parameter);
                    defaulted++;
                }
                else if (parameter.IsOptional)
                {
                    arguments[i] = Type.Missing;
                    defaulted++;
                }
                else
                {
                    reason = $"takes {Arity(parameters)} argument(s), {n} given";
                    return null;
                }
            }
            reason = "";
            return new Binding { Method = method, Arguments = arguments, ExactArity = n == k, Fit = fit, Defaulted = defaulted };
        }

        // An enum parameter's default comes back from reflection as its underlying number.
        private static object? DefaultOf(ParameterInfo parameter)
        {
            var value = parameter.DefaultValue;
            var type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
            var enumType = Nullable.GetUnderlyingType(type) ?? type;
            if (value != null && enumType.IsEnum && !enumType.IsInstanceOfType(value))
                return Enum.ToObject(enumType, value);
            return value;
        }

        private bool TryConvert(object? argument, Type parameterType, out object? value, out int fit, out string reason)
        {
            var type = parameterType.IsByRef ? parameterType.GetElementType()! : parameterType;
            value = null;
            fit = 0;
            reason = "";

            JsonElement json;
            if (argument is JsonElement element)
                json = element;
            else if (argument == null)
                json = JsonSerializer.SerializeToElement<object?>(null);
            else if (type.IsInstanceOfType(argument))
            {
                value = argument;
                fit = Natural;
                return true;
            }
            else if (argument is string || argument is IConvertible)
                json = JsonSerializer.SerializeToElement(argument, argument.GetType(), ScalarOptions);
            else
            {
                reason = $"{argument.GetType().Name} is not a {type.Name}";
                return false;
            }

            if (json.ValueKind == JsonValueKind.Null || json.ValueKind == JsonValueKind.Undefined)
            {
                if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
                {
                    reason = $"null is not a {type.Name}";
                    return false;
                }
                fit = Widened;
                return true;
            }

            // An object or an array is never a string argument: read as its text it would make every string overload
            // applicable — Log(string) would take the vector meant for Log(Vector3), and the first component with a string
            // method of that name would catch the call meant for the next one.
            if (type == typeof(string) && (json.ValueKind == JsonValueKind.Object || json.ValueKind == JsonValueKind.Array))
            {
                reason = $"a String, not a JSON {(json.ValueKind == JsonValueKind.Object ? "object" : "array")}";
                return false;
            }

            try
            {
                value = _serializer.JsonElementToObject(json, type);
                fit = FitOf(json, type);
                return true;
            }
            catch (Exception ex)
            {
                // call_method's schema asks for strings, so a vector arrives as the text of its JSON: read that text as JSON.
                if (json.ValueKind == JsonValueKind.String
                    && PropertyPathResolver.TryParseJson(json.GetString(), out var parsed)
                    && parsed.ValueKind != JsonValueKind.String)
                {
                    try
                    {
                        value = _serializer.JsonElementToObject(parsed, type);
                        fit = Lenient;
                        return true;
                    }
                    catch (Exception inner)
                    {
                        reason = Unwrapped(inner).Message;
                        return false;
                    }
                }
                reason = Unwrapped(ex).Message;
                return false;
            }
        }

        private static int FitOf(JsonElement json, Type parameterType)
        {
            var type = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
            if (type == typeof(object)) return Converted;
            bool numeric = (type.IsPrimitive && type != typeof(bool) && type != typeof(char)) || type == typeof(decimal);
            switch (json.ValueKind)
            {
                case JsonValueKind.String:
                    if (type == typeof(string) || type == typeof(char) || type.IsEnum
                        || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan))
                        return Natural;
                    return numeric || type == typeof(bool) ? Lenient : Converted;
                case JsonValueKind.Number:
                    if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                        return json.TryGetInt64(out _) ? Widened : Natural;
                    if (numeric) return Natural;
                    if (type.IsEnum) return Widened;
                    return type == typeof(string) ? Lenient : Converted;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    if (type == typeof(bool)) return Natural;
                    return type == typeof(string) ? Lenient : Converted;
                default:
                    return type == typeof(string) ? Lenient : Natural;
            }
        }

        private static object? Run(MethodInfo method, object? target, object?[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                // The engine's own exception and message, not "Exception has been thrown by the target of an invocation".
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static Exception Unwrapped(Exception ex) =>
            ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;

        private static PropertyPathException NoFit(string methodName, object?[]? args, IEnumerable<Type> searched,
            List<string> refusals, string kind = "")
        {
            var where = string.Join(", ", searched.Select(t => t.Name).Distinct());
            if (refusals.Count == 0)
                return new PropertyPathException($"{(kind.Length > 0 ? "Static method" : "Method")} '{methodName}' not found on {where}");

            int n = args?.Length ?? 0;
            var text = new StringBuilder();
            text.Append($"No {kind}overload of '{methodName}' takes the {n} argument(s) given");
            if (n > 0) text.Append(" (").Append(Brief(string.Join(", ", args!.Select(Shown)))).Append(')');
            text.Append(". Overloads:");
            const int maxListed = 12;
            foreach (var refusal in refusals.Take(maxListed))
                text.Append("\n  ").Append(refusal);
            if (refusals.Count > maxListed)
                text.Append($"\n  (+{refusals.Count - maxListed} more)");
            return new PropertyPathException(text.ToString());
        }

        private static string Shown(object? argument) => argument switch
        {
            null => "null",
            JsonElement json => json.GetRawText(),
            string s => JsonSerializer.Serialize(s),
            _ => Convert.ToString(argument, System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };

        private static string Brief(string text) => text.Length <= 120 ? text : text.Substring(0, 120) + "…";

        private static string Arity(ParameterInfo[] parameters)
        {
            int required = parameters.Count(p => !p.IsOptional && !p.HasDefaultValue);
            return required == parameters.Length ? $"{required}" : $"{required} to {parameters.Length}";
        }

        private static string Signature(MethodInfo method)
        {
            var parameters = method.GetParameters().Select(p =>
            {
                var text = $"{p.ParameterType.Name} {p.Name}";
                if (p.HasDefaultValue)
                    text += " = " + (p.DefaultValue == null ? "null" : Convert.ToString(DefaultOf(p), System.Globalization.CultureInfo.InvariantCulture));
                return text;
            });
            return $"{method.DeclaringType?.Name}.{method.Name}({string.Join(", ", parameters)})";
        }
    }
}
