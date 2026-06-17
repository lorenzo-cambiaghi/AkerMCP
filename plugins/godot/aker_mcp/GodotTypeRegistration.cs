#nullable enable
using System.Collections.Generic;
using Godot;
using AkerMcp.Shared.Serialization;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Registers JSON converters for Godot value types. JSON keys are kept
    /// lowercase (x/y/z, r/g/b/a) to match the convention used across AkerMCP,
    /// even though the C# struct members are PascalCase (X, Y, Z, R, G, B, A).
    /// </summary>
    public static class GodotTypeRegistration
    {
        private static bool _registered;

        public static void Register(TypeRegistry registry)
        {
            if (_registered) return;
            _registered = true;

            registry.RegisterCustomSerializer<Vector2>(
                v => new Dictionary<string, object?> { ["x"] = v.X, ["y"] = v.Y },
                d => new Vector2(F(d, "x"), F(d, "y"))
            );

            registry.RegisterCustomSerializer<Vector2I>(
                v => new Dictionary<string, object?> { ["x"] = v.X, ["y"] = v.Y },
                d => new Vector2I(I(d, "x"), I(d, "y"))
            );

            registry.RegisterCustomSerializer<Vector3>(
                v => new Dictionary<string, object?> { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z },
                d => new Vector3(F(d, "x"), F(d, "y"), F(d, "z"))
            );

            registry.RegisterCustomSerializer<Vector3I>(
                v => new Dictionary<string, object?> { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z },
                d => new Vector3I(I(d, "x"), I(d, "y"), I(d, "z"))
            );

            registry.RegisterCustomSerializer<Vector4>(
                v => new Dictionary<string, object?> { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z, ["w"] = v.W },
                d => new Vector4(F(d, "x"), F(d, "y"), F(d, "z"), F(d, "w"))
            );

            registry.RegisterCustomSerializer<Vector4I>(
                v => new Dictionary<string, object?> { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z, ["w"] = v.W },
                d => new Vector4I(I(d, "x"), I(d, "y"), I(d, "z"), I(d, "w"))
            );

            registry.RegisterCustomSerializer<Quaternion>(
                q => new Dictionary<string, object?> { ["x"] = q.X, ["y"] = q.Y, ["z"] = q.Z, ["w"] = q.W },
                d => new Quaternion(F(d, "x"), F(d, "y"), F(d, "z"), F(d, "w", 1f))
            );

            // Godot Color components are floats in 0..1.
            registry.RegisterCustomSerializer<Color>(
                c => new Dictionary<string, object?> { ["r"] = c.R, ["g"] = c.G, ["b"] = c.B, ["a"] = c.A },
                d => new Color(F(d, "r"), F(d, "g"), F(d, "b"), F(d, "a", 1f))
            );

            // Rect2 / Rect2I — flat {x, y, width, height} like Unity's Rect.
            registry.RegisterCustomSerializer<Rect2>(
                r => new Dictionary<string, object?>
                {
                    ["x"] = r.Position.X, ["y"] = r.Position.Y,
                    ["width"] = r.Size.X, ["height"] = r.Size.Y
                },
                d => new Rect2(F(d, "x"), F(d, "y"), F(d, "width"), F(d, "height"))
            );

            registry.RegisterCustomSerializer<Rect2I>(
                r => new Dictionary<string, object?>
                {
                    ["x"] = r.Position.X, ["y"] = r.Position.Y,
                    ["width"] = r.Size.X, ["height"] = r.Size.Y
                },
                d => new Rect2I(I(d, "x"), I(d, "y"), I(d, "width"), I(d, "height"))
            );

            // Aabb — nested {position, size} like Unity's Bounds.
            registry.RegisterCustomSerializer<Aabb>(
                b => new Dictionary<string, object?>
                {
                    ["position"] = new Dictionary<string, object?> { ["x"] = b.Position.X, ["y"] = b.Position.Y, ["z"] = b.Position.Z },
                    ["size"] = new Dictionary<string, object?> { ["x"] = b.Size.X, ["y"] = b.Size.Y, ["z"] = b.Size.Z }
                },
                d => new Aabb(Vec3(d, "position"), Vec3(d, "size"))
            );

            // Plane — {x, y, z} normal plus distance d.
            registry.RegisterCustomSerializer<Plane>(
                p => new Dictionary<string, object?> { ["x"] = p.Normal.X, ["y"] = p.Normal.Y, ["z"] = p.Normal.Z, ["d"] = p.D },
                d => new Plane(F(d, "x"), F(d, "y"), F(d, "z"), F(d, "d"))
            );
        }

        private static float F(Dictionary<string, object?> d, string key, float def = 0f)
            => d.TryGetValue(key, out var v) && v != null ? System.Convert.ToSingle(v) : def;

        private static int I(Dictionary<string, object?> d, string key, int def = 0)
            => d.TryGetValue(key, out var v) && v != null ? System.Convert.ToInt32(v) : def;

        private static Vector3 Vec3(Dictionary<string, object?> d, string key)
            => d.TryGetValue(key, out var v) && v is Dictionary<string, object?> sub
                ? new Vector3(F(sub, "x"), F(sub, "y"), F(sub, "z"))
                : Vector3.Zero;
    }
}
