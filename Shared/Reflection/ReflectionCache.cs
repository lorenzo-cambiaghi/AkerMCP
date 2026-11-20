using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace AkerMcp.Shared.Reflection
{
    public class ReflectionCache
    {
        public static readonly ReflectionCache Instance = new ReflectionCache();

        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        private readonly ConcurrentDictionary<(Type, string), PropertyInfo?> _propCache = new ConcurrentDictionary<(Type, string), PropertyInfo?>();
        private readonly ConcurrentDictionary<(Type, string), FieldInfo?> _fieldCache = new ConcurrentDictionary<(Type, string), FieldInfo?>();

        public PropertyInfo? GetProperty(Type type, string name)
            => _propCache.GetOrAdd((type, name), k => FindProperty(k.Item1, k.Item2));

        public FieldInfo? GetField(Type type, string name)
            => _fieldCache.GetOrAdd((type, name), k => FindField(k.Item1, k.Item2));

        public Type? GetMemberType(Type type, string name)
        {
            var prop = GetProperty(type, name);
            if (prop != null) return prop.PropertyType;

            var field = GetField(type, name);
            if (field != null) return field.FieldType;

            return null;
        }

        /// <summary>
        /// The public instance property called <paramref name="name"/>, in any case. Type.GetProperty(name, IgnoreCase)
        /// threw AmbiguousMatchException as soon as two candidates matched: a property re-declared with <c>new</c> and a
        /// different type, or two names that differ only in case (Unity's Projector has orthographicSize and the obsolete
        /// orthoGraphicSize) — and the whole get/set failed on a member that plainly exists. The exact spelling wins, then a
        /// member that is not obsolete, then the most derived declaration, which is the one code on that type would see.
        /// Indexers are not members a path can name.
        /// </summary>
        public static PropertyInfo? FindProperty(Type type, string name)
        {
            PropertyInfo? best = null;
            foreach (var property in type.GetProperties(PublicInstance))
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.GetIndexParameters().Length > 0) continue;
                if (best == null || Prefer(property, best, name)) best = property;
            }
            return best;
        }

        /// <summary>The public instance field called <paramref name="name"/>, chosen as <see cref="FindProperty"/> chooses.</summary>
        public static FieldInfo? FindField(Type type, string name)
        {
            FieldInfo? best = null;
            foreach (var field in type.GetFields(PublicInstance))
            {
                if (!string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || Prefer(field, best, name)) best = field;
            }
            return best;
        }

        private static bool Prefer(MemberInfo candidate, MemberInfo current, string name)
        {
            bool candidateExact = string.Equals(candidate.Name, name, StringComparison.Ordinal);
            bool currentExact = string.Equals(current.Name, name, StringComparison.Ordinal);
            if (candidateExact != currentExact) return candidateExact;

            bool candidateObsolete = candidate.IsDefined(typeof(ObsoleteAttribute), true);
            bool currentObsolete = current.IsDefined(typeof(ObsoleteAttribute), true);
            if (candidateObsolete != currentObsolete) return !candidateObsolete;

            return candidate.DeclaringType != null && current.DeclaringType != null
                && candidate.DeclaringType.IsSubclassOf(current.DeclaringType);
        }
    }
}
