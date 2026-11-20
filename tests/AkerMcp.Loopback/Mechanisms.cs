using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AkerMcp.Server;
using AkerMcp.Shared.Protocol;
using AkerMcp.Shared.Reflection;
using AkerMcp.Shared.Serialization;

namespace AkerMcp.Loopback
{
    /// <summary>
    /// Contracts of the value path — what a value that does not fit becomes, which member a read goes through, how long a
    /// forwarded call waits — checked on the shared code itself. The engine-specific halves (a Unity renderer, a Game view)
    /// cannot run here; the rules they delegate to can.
    /// </summary>
    internal static class Mechanisms
    {
        public static async Task Run(Func<string, object, Task<ToolResult>> call, Action<string, bool, string?> check)
        {
            MediaMechanisms.Run(check);
            Serializer(check);
            Resolver(check);
            SharedTwins(check);
            Deadlines(check);
            await ErrorsReachTheCaller(call, check);
        }

        // ---- GenericSerializer: a value that does not fit is an error, never a zero ----
        private static void Serializer(Action<string, bool, string?> check)
        {
            var registry = new TypeRegistry();
            // Shaped like Unity's registrations: a colour whose alpha defaults to 1, a box of two nested vectors, a mask of one member.
            registry.RegisterCustomSerializer<TestColor>(
                c => new Dictionary<string, object?> { ["r"] = c.R, ["g"] = c.G, ["b"] = c.B, ["a"] = c.A },
                d => new TestColor { R = F(d, "r"), G = F(d, "g"), B = F(d, "b"), A = F(d, "a", 1f) });
            registry.RegisterCustomSerializer<TestBox>(
                b => new Dictionary<string, object?> { ["center"] = Vec(b.Center), ["size"] = Vec(b.Size) },
                d => new TestBox { Center = Vec(d, "center"), Size = Vec(d, "size") });
            registry.RegisterCustomSerializer<TestMask>(
                m => new Dictionary<string, object?> { ["value"] = m.Value },
                d => new TestMask { Value = (int)F(d, "value") });
            var serializer = new GenericSerializer(registry);
            // A read that throws answers its message instead: a broken mechanism must turn a check red, not end the run.
            object? Read(string json, Type type)
            {
                try { return serializer.JsonElementToObject(Parse(json), type); }
                catch (Exception ex) { return ex.Message; }
            }

            var array = Error(Read("[1,2,3]", typeof(FakeVector)));
            check("a struct refuses a JSON array instead of reading it as zero", array.Contains("JSON object"), array);
            var extra = Error(Read("{\"x\":1,\"y\":2,\"z\":3,\"w\":4}", typeof(FakeVector)));
            check("a struct refuses a member it does not have", extra.Contains("no member w"), extra);
            var partial = Error(Read("{\"y\":5}", typeof(FakeVector)));
            check("a struct refuses a JSON object that leaves members out", partial.Contains("missing x, z"), partial);
            var whole = Read("{\"X\":1,\"y\":2,\"z\":3}", typeof(FakeVector));
            check("a struct given every member still reads, in any case",
                whole is FakeVector v && v.x == 1 && v.y == 2 && v.z == 3, whole?.ToString());

            var typo = Error(Read("\"Sollid\"", typeof(FakeMode)));
            check("an enum refuses a name it does not have, naming the ones it has", typo.Contains("SolidColor"), typo);
            var undefined = Error(Read("7", typeof(FakeMode)));
            check("an enum refuses a number it does not define", undefined.Contains("not a FakeMode"), undefined);
            check("a [Flags] enum takes a combination of its bits",
                Equals(Read("3", typeof(TestFlags)), TestFlags.A | TestFlags.B)
                && Equals(Read("\"a, b\"", typeof(TestFlags)), TestFlags.A | TestFlags.B), null);

            var opaque = Read("{\"r\":1,\"g\":0,\"b\":0}", typeof(TestColor));
            check("a registered struct may leave out a member its deserializer defaults (alpha 1)",
                opaque is TestColor c && c.R == 1 && c.A == 1, opaque?.ToString());
            var red = Error(Read("{\"r\":1}", typeof(TestColor)));
            check("a registered struct refuses a missing member that would read as zero",
                red.EndsWith("missing g, b"), red);
            var notAColour = Error(Read("\"#FF0000\"", typeof(TestColor)));
            check("a registered struct refuses text instead of reading it as black", notAColour.Contains("JSON object"), notAColour);

            var box = new TestBox { Center = new FakeVector(1, 2, 3), Size = new FakeVector(4, 5, 6) };
            var roundTrip = Read(serializer.ObjectToJsonElement(box).GetRawText(), typeof(TestBox));
            check("a registered struct of nested objects reads back what it wrote (Bounds, Aabb)",
                roundTrip is TestBox back && back.Center.y == 2 && back.Size.z == 6, roundTrip?.ToString());
            var nestedPartial = Error(Read("{\"center\":{\"x\":1},\"size\":{\"x\":1,\"y\":1,\"z\":1}}", typeof(TestBox)));
            check("a nested member left out is named with its path", nestedPartial.Contains("center.y"), nestedPartial);
            var mask = Read("256", typeof(TestMask));
            check("a struct of one member takes the bare value (a LayerMask as 256)", mask is TestMask m && m.Value == 256, mask?.ToString());

            var id = Guid.NewGuid();
            var idJson = serializer.ObjectToJsonElement(id);
            check("a Guid is written as its text, not taken apart as a struct",
                idJson.ValueKind == JsonValueKind.String && idJson.GetString() == id.ToString()
                && Equals(Read(idJson.GetRawText(), typeof(Guid)), id), idJson.GetRawText());
        }

        /// <summary>What a refused read answered, or "(no error)" when it produced a value.</summary>
        private static string Error(object? read) => read as string ?? "(no error)";

        // ---- PropertyPathResolver: the reason reaches the message, and the member keeps its value ----
        private static void Resolver(Action<string, bool, string?> check)
        {
            var state = new FakeProbeState { Mode = FakeMode.SolidColor, Position = new FakeVector(1, 2, 3) };
            var resolver = new PropertyPathResolver();

            var typo = Fails(() => resolver.Set(state, "Mode", "Sollid"), out var typoError);
            check("set through the resolver refuses an enum typo as a value error, with the names",
                typoError is PropertyValueException && typo.Contains("SolidColor") && state.Mode == FakeMode.SolidColor, typo);
            var array = Fails(() => resolver.Set(state, "Position", "[9,9,9]"), out var arrayError);
            check("set through the resolver refuses a vector given as an array and leaves it as it was",
                arrayError is PropertyValueException && state.Position.x == 1 && state.Position.z == 3, array);
        }

        // ---- The shared twin: which member a read goes through when the getter would make a copy ----
        private static void SharedTwins(Action<string, bool, string?> check)
        {
            check("a member with a shared twin reads through it (material, materials, fontMaterial)",
                SharedMembers.SharedTwin(typeof(TestRenderer), "material") == "sharedMaterial"
                && SharedMembers.SharedTwin(typeof(TestRenderer), "materials") == "sharedMaterials"
                && SharedMembers.SharedTwin(typeof(TestText), "fontMaterial") == "fontSharedMaterial", null);
            check("a member without a twin, or the twin itself, reads as it is",
                SharedMembers.SharedTwin(typeof(TestRenderer), "enabled") == null
                && SharedMembers.SharedTwin(typeof(TestRenderer), "sharedMaterial") == null, null);
            var nested = SharedMembers.ToSharedPath(typeof(TestRenderer), "material.color");
            var indexed = SharedMembers.ToSharedPath(typeof(TestRenderer), "materials[0].color");
            check("a path is rewritten member by member, index kept",
                nested == "sharedMaterial.color" && indexed == "sharedMaterials[0].color", nested + " / " + indexed);
            var unresolved = SharedMembers.ToSharedPath(typeof(TestRenderer), "nosuch.material");
            check("a path that stops resolving is left to the resolver to report", unresolved == "nosuch.material", unresolved);
        }

        // ---- EngineConnection: the deadline follows timeout_ms all the way up ----
        private static void Deadlines(Action<string, bool, string?> check)
        {
            using var connection = new EngineConnection();
            var huge = connection.RequestTimeoutFor(Parse("{\"timeout_ms\":" + int.MaxValue + "}"));
            check("a huge timeout_ms does not wrap the deadline back to the default", huge == int.MaxValue, huge.ToString());
            var minute = connection.RequestTimeoutFor(Parse("{\"timeout_ms\":60000}"));
            check("timeout_ms gets the reply margin on top", minute == 65_000, minute.ToString());
            var skew = EngineConnection.DescribeVersionSkew("1.10.0", "1.7.0");
            check("version skew compares by number (1.10 is newer than 1.7)",
                !skew.breaking && skew.message != null && skew.message.Contains("after the server's version"), skew.message);
        }

        // ---- Through the pipe: a refused value and an unknown level come back as errors ----
        private static async Task ErrorsReachTheCaller(Func<string, object, Task<ToolResult>> call, Action<string, bool, string?> check)
        {
            var before = FakeProbeNode.State.Position;
            var set = await call("set_property", new { object_path = FakeProbeNode.ProbePath, property_path = "Position", value = new[] { 9, 9, 9 } });
            var after = FakeProbeNode.State.Position;
            check("set_property answers an error for a vector given as an array, and leaves it as it was",
                set.IsError && Text(set).Contains("JSON object") && after.x == before.x && after.y == before.y && after.z == before.z, Text(set));

            var plural = await call("get_console_logs", new { level_filter = "warnings", count = 5 });
            check("get_console_logs reads a plural level as the level", !plural.IsError && Text(plural).Contains("loopback warning marker"), Text(plural));
            var unknown = await call("get_console_logs", new { level_filter = "loud" });
            check("get_console_logs refuses an unknown level, naming the ones there are",
                unknown.IsError && Text(unknown).Contains("error, warning, info, debug or all"), Text(unknown));
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static string Text(ToolResult r) => r.Content.Count > 0 ? (r.Content[0].Text ?? "") : "";

        private static string Fails(Action action) => Fails(action, out _);

        /// <summary>The exception's message, or "(no error)" when the action went through.</summary>
        private static string Fails(Action action, out Exception? error)
        {
            try { action(); error = null; return "(no error)"; }
            catch (Exception ex) { error = ex; return ex.Message; }
        }

        private static float F(Dictionary<string, object?> d, string key, float def = 0f) =>
            d.TryGetValue(key, out var v) && v != null ? Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture) : def;

        private static Dictionary<string, object?> Vec(FakeVector v) => new Dictionary<string, object?> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        private static FakeVector Vec(Dictionary<string, object?> d, string key) =>
            d.TryGetValue(key, out var v) && v is Dictionary<string, object?> sub ? new FakeVector(F(sub, "x"), F(sub, "y"), F(sub, "z")) : default;
    }

    public struct TestColor { public float R, G, B, A; public override string ToString() => $"{R} {G} {B} {A}"; }
    public struct TestBox { public FakeVector Center, Size; public override string ToString() => $"{Center.x},{Center.y},{Center.z} / {Size.x},{Size.y},{Size.z}"; }
    public struct TestMask { public int Value; }

    [Flags]
    public enum TestFlags { None = 0, A = 1, B = 2 }

    /// <summary>Shaped like UnityEngine.Renderer: getters that copy, next to their shared twins.</summary>
    public sealed class TestRenderer
    {
        public TestMaterial material => new TestMaterial();
        public TestMaterial sharedMaterial => new TestMaterial();
        public TestMaterial[] materials => new TestMaterial[0];
        public TestMaterial[] sharedMaterials => new TestMaterial[0];
        public bool enabled => true;
    }

    /// <summary>Shaped like TextMeshPro's TMP_Text: the twin of fontMaterial is fontSharedMaterial.</summary>
    public sealed class TestText
    {
        public TestMaterial fontMaterial => new TestMaterial();
        public TestMaterial fontSharedMaterial => new TestMaterial();
    }

    public sealed class TestMaterial
    {
        public float color => 1f;
    }
}
