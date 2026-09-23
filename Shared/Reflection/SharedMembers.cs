using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace AkerMcp.Shared.Reflection
{
    /// <summary>
    /// Members whose getter hands out a copy it makes on the spot, recognised by the shared twin next to them: Unity's
    /// Renderer.material / sharedMaterial, MeshFilter.mesh / sharedMesh, Collider.material / sharedMaterial, TextMeshPro's
    /// fontMaterial / fontSharedMaterial. Reading one of them in the editor instantiates a material or a mesh and assigns
    /// it to the object — a read that changes the scene it describes. The twin reads the same thing without the copy.
    /// A rule on the pair rather than a list of names, so a component the list never heard of (TextMeshPro was one) is
    /// covered by following the same convention.
    /// </summary>
    public static class SharedMembers
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        /// <summary>The name of the readable shared twin of <paramref name="memberName"/> on <paramref name="type"/>, or null.</summary>
        public static string? SharedTwin(Type type, string memberName)
        {
            if (string.IsNullOrEmpty(memberName)) return null;
            foreach (var candidate in TwinNames(memberName))
            {
                var twin = FindProperty(type, candidate);
                if (twin != null && twin.CanRead) return twin.Name;
            }
            return null;
        }

        /// <summary>
        /// <paramref name="path"/> with every member that has a shared twin replaced by the twin, following the member types
        /// down the path ("MeshRenderer" + "material.color" reads "sharedMaterial.color"; "materials[0]" reads
        /// "sharedMaterials[0]"). The rewriting stops where the path stops resolving: the resolver reports that part.
        /// </summary>
        public static string ToSharedPath(Type rootType, string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var segments = path.Split('.');
            Type? type = rootType;
            for (int i = 0; i < segments.Length && type != null; i++)
            {
                var segment = segments[i];
                int bracket = segment.IndexOf('[');
                var name = bracket >= 0 ? segment.Substring(0, bracket) : segment;
                var index = bracket >= 0 ? segment.Substring(bracket) : "";
                if (name.Length == 0) { type = ElementType(type); continue; }

                Type? memberType;
                var property = FindProperty(type, name);
                if (property != null)
                {
                    var twin = SharedTwin(type, property.Name);
                    if (twin != null)
                    {
                        segments[i] = twin + index;
                        property = FindProperty(type, twin)!;
                    }
                    memberType = property.PropertyType;
                }
                else
                {
                    memberType = type.GetField(name, Flags)?.FieldType;
                }

                type = memberType != null && index.Length > 0 ? ElementType(memberType) : memberType;
            }
            return string.Join(".", segments);
        }

        private static IEnumerable<string> TwinNames(string name)
        {
            yield return "shared" + name;                       // material → sharedMaterial (the lookup ignores case)
            if (name.Length > 4 && name.StartsWith("font", StringComparison.OrdinalIgnoreCase))
                yield return "fontShared" + name.Substring(4);  // fontMaterial → fontSharedMaterial
        }

        private static PropertyInfo? FindProperty(Type type, string name)
        {
            try
            {
                var property = type.GetProperty(name, Flags);
                return property != null && property.GetIndexParameters().Length == 0 ? property : null;
            }
            catch (AmbiguousMatchException)
            {
                return null;
            }
        }

        private static Type? ElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && typeof(IList).IsAssignableFrom(type)) return type.GetGenericArguments()[0];
            return null;
        }
    }
}
