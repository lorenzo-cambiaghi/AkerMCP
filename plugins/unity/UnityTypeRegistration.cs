#nullable enable

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using AkerMcp.Shared.Serialization;

namespace AkerMcp.Unity
{
    public static class UnityTypeRegistration
    {
        private static bool _registered;

        public static void Register(TypeRegistry registry)
        {
            if (_registered) return;
            _registered = true;

            // A GameObject, a component or an asset is never built from JSON: the serializer would run new GameObject()
            // (a stray object in the scene) or new MonoBehaviour() (Unity refuses it with a warning), and call_method tries
            // the conversion on every overload. It is written by reading its members, without the getters that copy.
            registry.RegisterReferenceOnly(typeof(UnityEngine.Object));

            // Vector2
            registry.RegisterCustomSerializer<Vector2>(
                v => new Dictionary<string, object?> { ["x"] = v.x, ["y"] = v.y },
                d => new Vector2(F(d, "x"), F(d, "y"))
            );

            // Vector3
            registry.RegisterCustomSerializer<Vector3>(
                v => new Dictionary<string, object?> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z },
                d => new Vector3(F(d, "x"), F(d, "y"), F(d, "z"))
            );

            // Vector4
            registry.RegisterCustomSerializer<Vector4>(
                v => new Dictionary<string, object?> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z, ["w"] = v.w },
                d => new Vector4(F(d, "x"), F(d, "y"), F(d, "z"), F(d, "w"))
            );

            // Vector2Int
            registry.RegisterCustomSerializer<Vector2Int>(
                v => new Dictionary<string, object?> { ["x"] = v.x, ["y"] = v.y },
                d => new Vector2Int(I(d, "x"), I(d, "y"))
            );

            // Vector3Int
            registry.RegisterCustomSerializer<Vector3Int>(
                v => new Dictionary<string, object?> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z },
                d => new Vector3Int(I(d, "x"), I(d, "y"), I(d, "z"))
            );

            // Quaternion
            registry.RegisterCustomSerializer<Quaternion>(
                q => new Dictionary<string, object?> { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w },
                d => new Quaternion(F(d, "x"), F(d, "y"), F(d, "z"), F(d, "w"))
            );

            // Color
            registry.RegisterCustomSerializer<Color>(
                c => new Dictionary<string, object?> { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a },
                d => new Color(F(d, "r"), F(d, "g"), F(d, "b"), F(d, "a", 1f))
            );

            // Color32
            registry.RegisterCustomSerializer<Color32>(
                c => new Dictionary<string, object?> { ["r"] = (int)c.r, ["g"] = (int)c.g, ["b"] = (int)c.b, ["a"] = (int)c.a },
                d => new Color32(B(d, "r"), B(d, "g"), B(d, "b"), B(d, "a", 255))
            );

            // Rect
            registry.RegisterCustomSerializer<Rect>(
                r => new Dictionary<string, object?> { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height },
                d => new Rect(F(d, "x"), F(d, "y"), F(d, "width"), F(d, "height"))
            );

            // RectInt
            registry.RegisterCustomSerializer<RectInt>(
                r => new Dictionary<string, object?> { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height },
                d => new RectInt(I(d, "x"), I(d, "y"), I(d, "width"), I(d, "height"))
            );

            // Bounds
            registry.RegisterCustomSerializer<Bounds>(
                b => new Dictionary<string, object?>
                {
                    ["center"] = new Dictionary<string, object?> { ["x"] = b.center.x, ["y"] = b.center.y, ["z"] = b.center.z },
                    ["size"] = new Dictionary<string, object?> { ["x"] = b.size.x, ["y"] = b.size.y, ["z"] = b.size.z }
                },
                d =>
                {
                    var center = Vec3(d, "center");
                    var size = Vec3(d, "size");
                    return new Bounds(center, size);
                }
            );

            // BoundsInt
            registry.RegisterCustomSerializer<BoundsInt>(
                b => new Dictionary<string, object?>
                {
                    ["position"] = new Dictionary<string, object?> { ["x"] = b.position.x, ["y"] = b.position.y, ["z"] = b.position.z },
                    ["size"] = new Dictionary<string, object?> { ["x"] = b.size.x, ["y"] = b.size.y, ["z"] = b.size.z }
                },
                d =>
                {
                    var pos = Vec3I(d, "position");
                    var size = Vec3I(d, "size");
                    return new BoundsInt(pos, size);
                }
            );

            // LayerMask
            registry.RegisterCustomSerializer<LayerMask>(
                l => new Dictionary<string, object?> { ["value"] = l.value },
                d => new LayerMask { value = I(d, "value") }
            );
        }

        // Helpers: a member from the dict. Invariant culture: a number given as text ({"x":"0.5"}) read with the editor's
        // culture was 5 under it-IT, the fault set_property already fixed for bare numbers.
        private static float F(Dictionary<string, object?> d, string key, float def = 0f)
        {
            if (d.TryGetValue(key, out var v) && v != null)
                return System.Convert.ToSingle(v, CultureInfo.InvariantCulture);
            return def;
        }

        private static int I(Dictionary<string, object?> d, string key, int def = 0)
        {
            if (d.TryGetValue(key, out var v) && v != null)
                return System.Convert.ToInt32(v, CultureInfo.InvariantCulture);
            return def;
        }

        private static byte B(Dictionary<string, object?> d, string key, byte def = 0)
        {
            if (d.TryGetValue(key, out var v) && v != null)
                return System.Convert.ToByte(v, CultureInfo.InvariantCulture);
            return def;
        }

        // Helper: extract Vector3 from nested dict
        private static Vector3 Vec3(Dictionary<string, object?> d, string key)
        {
            if (d.TryGetValue(key, out var v) && v is Dictionary<string, object?> sub)
                return new Vector3(F(sub, "x"), F(sub, "y"), F(sub, "z"));
            return Vector3.zero;
        }

        // Helper: extract Vector3Int from nested dict
        private static Vector3Int Vec3I(Dictionary<string, object?> d, string key)
        {
            if (d.TryGetValue(key, out var v) && v is Dictionary<string, object?> sub)
                return new Vector3Int(I(sub, "x"), I(sub, "y"), I(sub, "z"));
            return Vector3Int.zero;
        }
    }
}
