using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Client;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Ipc;
using AkerMcp.Shared.Reflection;
using AkerMcp.Shared.Scripting;
using AkerMcp.Shared.Serialization;

namespace AkerMcp.Loopback
{
    /// <summary>
    /// The contracts behind call_method, set_property, create, inspect, execute and write_script: which overload a call
    /// reaches and what it is refused for, how an argument or a value becomes the member's type, what a write through an
    /// index or into a struct in a collection does, what the snippet splitter lifts out of a script, and what the request
    /// handler answers. Checked on the shared code and on the handler itself, with fakes shaped like the engine types the
    /// failures were found on.
    /// </summary>
    internal static class ValueMechanisms
    {
        public static async Task Run(Action<string, bool, string?> check)
        {
            Overloads(check);
            Paths(check);
            Lookup(check);
            Serializer(check);
            Splitter(check);
            await Handler(check);
        }

        // ---- MethodInvoker: the overload the arguments fit, or an error naming the ones there are ----
        private static void Overloads(Action<string, bool, string?> check)
        {
            var invoker = new MethodInvoker();
            var body = new VmBody();
            string Call(object[] targets, string method, params object?[] args)
            {
                body.Last = "";
                try { invoker.Invoke(targets, method, args); return body.Last; }
                catch (Exception ex) { return "error: " + ex.GetType().Name + ": " + ex.Message; }
            }
            string On(string method, params object?[] args) => Call(new object[] { body }, method, args);

            var vector = On("AddForce", J("{\"x\":1,\"y\":2,\"z\":3}"));
            check("an overloaded method is reachable: a vector picks AddForce(Vector3)", vector == "vec 1,2,3", vector);
            var withMode = On("AddForce", J("{\"x\":1,\"y\":2,\"z\":3}"), J("\"SolidColor\""));
            var xyz = On("AddForce", J("1"), J("2"), J("3"));
            check("overloads are told apart by their arguments: (Vector3, enum by name) and (float, float, float)",
                withMode == "vec 1,2,3 SolidColor" && xyz == "xyz 1,2,3", withMode + " / " + xyz);

            var byName = On("Play", J("\"walk\""));
            var byHash = On("Play", J("5"));
            check("a string picks Play(string), a number Play(int), the layer from its declared default",
                byName == "name walk -1" && byHash == "hash 5 -1", byName + " / " + byHash);

            var other = new VmOther();
            var second = Call(new object[] { other, body }, "AddForce", J("{\"x\":1,\"y\":2,\"z\":3}"));
            check("the first target with an overload the arguments fit is the one called (a component, then the GameObject)",
                second == "vec 1,2,3" && other.Calls == 0, second);

            var none = On("SetActive");
            check("a missing required argument is an error listing the signature, never a default (SetActive() is not SetActive(false))",
                none.StartsWith("error") && none.Contains("SetActive(Boolean value)") && body.Last == "", none);
            var extra = On("SetActive", J("true"), J("5"));
            check("an extra argument is an error, never dropped", extra.StartsWith("error") && extra.Contains("2 argument"), extra);

            var generic = On("GetNode", J("\"Child\""));
            check("a generic method definition is skipped for its plain twin (GetNode<T> beside GetNode)", generic == "node Child", generic);

            var thrown = On("Warp", J("{\"x\":0,\"y\":0,\"z\":0}"));
            check("the method's own exception reaches the caller, not TargetInvocationException",
                thrown == "error: InvalidOperationException: the agent is not on a NavMesh", thrown);

            var sum = On("Sum", J("1"), J("2"), J("3"));
            check("a params array takes the trailing arguments", sum == "sum 6", sum);

            var bus = On("Bus", J("\"SFX\""));
            check("a string reaches a parameter through the type's implicit operator (Godot's StringName)", bus == "bus SFX", bus);

            var text = On("AddForce", J("\"{\\\"x\\\":1,\\\"y\\\":2,\\\"z\\\":3}\""));
            var flag = On("SetActive", J("\"false\""));
            check("arguments given as strings, as call_method's schema asks, are read as JSON (a vector, a boolean)",
                text == "vec 1,2,3" && flag == "active False", text + " / " + flag);

            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("it-IT");
            string scale;
            try { scale = On("Scale", J("\"0.5\"")); }
            finally { CultureInfo.CurrentCulture = culture; }
            check("a number given as text is read in the invariant culture (it-IT: \"0.5\" is not 5)", scale == "scale 0.5", scale);

            var missing = Call(new object[] { other, body }, "Jump");
            check("a method no target has is an error naming the targets searched",
                missing.Contains("'Jump' not found") && missing.Contains("VmOther") && missing.Contains("VmBody"), missing);
        }

        // ---- PropertyPathResolver: indexes, structs in collections, quoted keys, setters ----
        private static void Paths(Action<string, bool, string?> check)
        {
            var resolver = new PropertyPathResolver();
            var state = new VmState();

            var converted = Fails(() =>
            {
                resolver.Set(state, "Numbers[1]", "7");
                resolver.Set(state, "Modes[0]", "SolidColor");
                resolver.Set(state, "Weights[\"k\"]", "0.5");
            });
            check("a write through an index converts to the element type (int[] and a dictionary of floats from text, a list of enums by name)",
                state.Numbers[1] == 7 && state.Modes[0] == FakeMode.SolidColor && state.Weights.TryGetValue("k", out var k) && k == 0.5f, converted);

            var range = Fails(() => resolver.Set(state, "Numbers[5]", "1"));
            check("an index past the end is an out-of-range error naming the count", range.Contains("out of range") && range.Contains("3 elements"), range);

            var structs = Fails(() =>
            {
                resolver.Set(state, "PointArray[0].x", "5");
                resolver.Set(state, "PointList[1].y", "6");
            });
            check("a struct inside an array or a list is written back (points[0].x = 5)",
                state.PointArray[0].x == 5 && state.PointList[1].y == 6, structs);

            var dotted = Fails(() => resolver.Set(state, "Weights[\"a.b\"]", "2"));
            object? dottedRead = null;
            var dottedReadError = Fails(() => dottedRead = resolver.Resolve(state, "Weights[\"a.b\"]"));
            check("a quoted key holding a dot is one segment, written and read",
                state.Weights.TryGetValue("a.b", out var ab) && ab == 2f && Equals(dottedRead, 2f), dotted + " / " + dottedReadError);

            var types = new[]
            {
                resolver.GetTargetType(typeof(VmState), "PointArray[0].x"),
                resolver.GetTargetType(typeof(VmState), "Weights[\"k\"]"),
                resolver.GetTargetType(typeof(VmState), "Modes[0]"),
            };
            check("the member type is found through an index", types[0] == typeof(float) && types[1] == typeof(float) && types[2] == typeof(FakeMode),
                string.Join(", ", types.Select(t => t?.Name ?? "null")));

            var setter = Fails(() => resolver.Set(state, "Guarded", "1"), out var setterError);
            check("a setter's own exception reaches the caller, not TargetInvocationException",
                setterError is ArgumentException && setter == "the setter refused it", setterError?.GetType().Name + ": " + setter);

            var list = Fails(() => resolver.Set(state, "Waypoints", "[{\"x\":1,\"y\":2,\"z\":3}]"));
            check("a list member takes a JSON array given as text (what set_property sends for a Unity component's List<Vector3>)",
                state.Waypoints.Count == 1 && state.Waypoints[0].y == 2, list);
        }

        // ---- ReflectionCache: one member where GetProperty(name, IgnoreCase) threw AmbiguousMatchException ----
        private static void Lookup(Action<string, bool, string?> check)
        {
            PropertyInfoName? Find(Type type, string name)
            {
                try { var p = ReflectionCache.Instance.GetProperty(type, name); return p == null ? null : new PropertyInfoName(p.Name, p.DeclaringType!); }
                catch (Exception ex) { return new PropertyInfoName("threw " + ex.GetType().Name, typeof(void)); }
            }

            var shadowed = Find(typeof(VmDerived), "size");
            var derived = new VmDerived();
            var write = Fails(() => new PropertyPathResolver().Set(derived, "Size", "1.5"));
            check("a property re-declared with new and another type resolves to the most derived one",
                shadowed?.Declaring == typeof(VmDerived) && derived.Size == 1.5f, shadowed?.Name + " / " + write);

            var exact = Find(typeof(VmProjector), "orthoGraphicSize");
            var current = Find(typeof(VmProjector), "OrthographicSize");
            check("names differing only in case: the exact spelling wins, otherwise the one that is not obsolete (Projector)",
                exact?.Name == "orthoGraphicSize" && current?.Name == "orthographicSize", exact?.Name + " / " + current?.Name);

            var fields = new ReflectionInspector().InspectType(typeof(FakeVector)).Properties.Select(p => p.Name).ToList();
            check("inspecting a struct type lists its public fields (Vector3: x, y, z)",
                fields.Contains("x") && fields.Contains("y") && fields.Contains("z"), string.Join(", ", fields));
        }

        private sealed class PropertyInfoName
        {
            public PropertyInfoName(string name, Type declaring) { Name = name; Declaring = declaring; }
            public string Name { get; }
            public Type Declaring { get; }
        }

        // ---- GenericSerializer: fit or error, interfaces, conversions, and writes that used to throw ----
        private static void Serializer(Action<string, bool, string?> check)
        {
            var registry = new TypeRegistry();
            registry.RegisterReferenceOnly(typeof(VmEngineObject));
            var serializer = new GenericSerializer(registry);
            string? Error(string json, Type type)
            {
                try { serializer.JsonElementToObject(J(json), type); return null; }
                catch (Exception ex) { return ex.Message; }
            }
            object? Read(string json, Type type)
            {
                try { return serializer.JsonElementToObject(J(json), type); }
                catch (Exception ex) { return "error: " + ex.Message; }
            }

            var overflow = new[]
            {
                Error("3000000000", typeof(int)), Error("300", typeof(byte)), Error("1.5", typeof(int)),
                Error("-1", typeof(uint)), Error("\"70000\"", typeof(short)),
            };
            var max = Read("18446744073709551615", typeof(ulong));
            check("a whole number that does not fit is an error, not a wrap (3000000000 int, 300 byte, 1.5 int, -1 uint), and ulong.MaxValue reads whole",
                overflow.All(e => e != null && (e.Contains("does not fit") || e.Contains("whole number"))) && Equals(max, ulong.MaxValue),
                string.Join(" | ", overflow.Select(e => e ?? "(no error)")) + " | " + max);

            var nullMember = Error("{\"x\":null,\"y\":1,\"z\":2}", typeof(FakeVector));
            check("null for a value-type member is an error, not a zero", nullMember != null && nullMember.Contains("null"), nullMember ?? "(no error)");

            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("it-IT");
            object? point, comma;
            try { point = Read("\"0.5\"", typeof(float)); comma = Read("\"0,5\"", typeof(float)); }
            finally { CultureInfo.CurrentCulture = culture; }
            check("a number in text is read in the invariant culture, and \"0,5\" is refused, not read as 5 (it-IT)",
                Equals(point, 0.5f) && comma is string c && c.StartsWith("error"), point + " / " + comma);

            var ilist = Read("[1,2]", typeof(IList<int>));
            var readOnly = Read("[3]", typeof(IReadOnlyList<int>));
            var dict = Read("{\"a\":1}", typeof(IDictionary<string, int>));
            var readOnlyDict = Read("{\"b\":2}", typeof(IReadOnlyDictionary<string, int>));
            check("a member declared as IList<T>, IReadOnlyList<T>, IDictionary<string,T> or IReadOnlyDictionary<string,T> gets a List or a Dictionary",
                ilist is List<int> { Count: 2 } && readOnly is List<int> { Count: 1 }
                && dict is Dictionary<string, int> d1 && d1["a"] == 1 && readOnlyDict is Dictionary<string, int> d2 && d2["b"] == 2,
                $"{ilist} / {readOnly} / {dict} / {readOnlyDict}");

            var notObject = Error("5", typeof(Dictionary<string, int>));
            check("a dictionary refuses a value that is not an object, instead of becoming empty", notObject != null, notObject ?? "(no error)");

            var name = Read("\"SFX\"", typeof(VmName));
            check("a string becomes a type through its implicit operator (Godot's StringName, NodePath)",
                name is VmName n && n.ToString() == "SFX", name?.ToString());
            var asset = Read("\"someAsset\"", typeof(FakeAsset));
            check("a string for a class without a conversion is refused, never an empty instance", asset is string a && a.StartsWith("error"), asset?.ToString());

            VmEngineNode.Built = 0;
            var engine = Error("{}", typeof(VmEngineNode));
            var cleared = Read("null", typeof(VmEngineNode));
            check("an engine object is never built from JSON (its constructor never runs), and null still clears it",
                engine != null && engine.Contains("engine object") && VmEngineNode.Built == 0 && cleared == null, engine ?? "(no error)");

            string Write(object value)
            {
                try { return serializer.ObjectToJsonElement(value).GetRawText(); }
                catch (Exception ex) { return "error: " + ex.GetType().Name + ": " + ex.Message; }
            }
            var grid = Write(new[,] { { 1, 2 }, { 3, 4 } });
            check("a multi-dimensional array is written as nested lists", grid == "[[1,2],[3,4]]", grid);
            var rows = Write(new List<Dictionary<string, int>> { new Dictionary<string, int> { ["a"] = 1 } });
            check("a dictionary inside a list is written as an object", rows == "[{\"a\":1}]", rows);
            var holder = Write(new VmHolder());
            check("a class holding a struct with a view of itself is written, not thrown (a component holding a Vector3)",
                !holder.StartsWith("error") && holder.Contains("\"y\":2"), holder);
            var infinite = Write(float.PositiveInfinity);
            check("an infinity is written, not thrown", infinite.Contains("Infinity"), infinite);
        }

        // ---- ScriptSourceSplitter: what is lifted to file scope ----
        private static void Splitter(Action<string, bool, string?> check)
        {
            var local = ScriptSourceSplitter.Split("var record = new System.Text.StringBuilder();\nrecord.Append(\"x\");\nreturn record.ToString();");
            check("a local called record stays in the body", local.Types.Trim().Length == 0 && local.Body.Contains("record.Append"), local.Types);

            var declared = ScriptSourceSplitter.Split("record Point(int X, int Y);\nrecord struct Pair { }\nreturn new Point(1, 2);");
            check("a record declaration is still lifted (record Point(...), record struct Pair)",
                declared.Types.Contains("record Point(int X, int Y);") && declared.Types.Contains("record struct Pair") && !declared.Body.Contains("record"),
                declared.Types);

            var commented = ScriptSourceSplitter.Split("/* header\n   two lines */\nusing System.IO; // files\nusing System.Text;\nreturn 1;");
            check("usings under a block-comment header and with a trailing comment are hoisted",
                commented.Usings.Contains("using System.IO;") && commented.Usings.Contains("using System.Text;") && !commented.Body.Contains("using"),
                commented.Usings + " || " + commented.Body);

            var declaration = ScriptSourceSplitter.Split("using System.IDisposable d = disposable;\nreturn 1;");
            check("an explicitly typed local using declaration stays in the body",
                declaration.Usings.Length == 0 && declaration.Body.Contains("using System.IDisposable d"), declaration.Usings);

            var aliases = ScriptSourceSplitter.Split("global using System.Linq;\nusing Pair = (int A, int B);\nreturn 1;");
            check("a tuple alias is hoisted, and a global using as a plain one",
                aliases.Usings.Contains("using Pair = (int A, int B);") && aliases.Usings.Contains("using System.Linq;")
                && !aliases.Usings.Contains("global") && !aliases.Body.Contains("using"), aliases.Usings);

            var raw = ScriptSourceSplitter.Split("var s = \"\"\"\n  using System;\n  public class Inside { }\n  \"\"\";\nreturn s;");
            check("a raw string literal is skipped: a class written inside it stays inside", raw.Types.Trim().Length == 0 && raw.Body.Contains("class Inside"), raw.Types);

            var hole = ScriptSourceSplitter.Split("var s = $\"{Fmt(\"{\")}\";\nclass Helper { }\nreturn s;");
            check("an interpolated string whose hole holds a string is skipped as one literal", hole.Types.Contains("class Helper"), hole.Types + " || " + hole.Body);
        }

        // ---- The request handler: arguments, values, refusals and the reply's shape ----
        private static async Task Handler(Action<string, bool, string?> check)
        {
            var graph = new VmGraph();
            var node = graph.Add(new VmNode("/V"));
            graph.Add(new VmNode("/Big", extraProperties: 3000));
            var project = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aker-values-" + Guid.NewGuid().ToString("N"), "Project");
            System.IO.Directory.CreateDirectory(project);
            var editor = new VmEditor { ProjectPath = project };
            var handler = new IpcRequestHandler(graph, new FakeCapabilities(), new VmDispatcher(), new ClientConfiguration(),
                editorContext: editor, codeExecutor: new FakeCodeExecutor());
            var locked = new IpcRequestHandler(graph, new FakeCapabilities(), new VmDispatcher(),
                new ClientConfiguration { EnableExecuteTool = false }, editorContext: editor, codeExecutor: new FakeCodeExecutor());

            try
            {
                await Ask(handler, "call_method", new { target = "/V", method = "Log", args = new object[] { new { x = 1, y = 2, z = 3 } } });
                check("call_method hands an argument over as JSON: an object reaches Log(Vector3), not Log(string) as its text",
                    node.Body.Last == "log vector 2", node.Body.Last);

                node.State.Asset = new FakeAsset();
                var cleared = await Ask(handler, "set_property", new { object_path = "/V", property_path = "Asset", value = (object?)null });
                check("set_property with null clears a reference", cleared.Success && node.State.Asset == null, Reply(cleared));

                var missing = await Ask(handler, "set_property", new { object_path = "/Nobody", property_path = "Scale", value = 1 });
                check("set_property on an object that does not exist is an error response", !missing.Success && Reply(missing).Contains("Object not found"), Reply(missing));

                var waypoints = await Ask(handler, "set_property", new
                {
                    object_path = "/V", property_path = "Waypoints",
                    value = new[] { new { x = 1, y = 2, z = 3 }, new { x = 4, y = 5, z = 6 } }
                });
                check("set_property writes a list member the node's object cannot type (a Unity component's List<Vector3>)",
                    waypoints.Success && node.State.Waypoints.Count == 2 && node.State.Waypoints[1].z == 6, Reply(waypoints));

                var created = await Ask(handler, "create", new { type = "Thing", name = "Made", properties = new { Mode = "Sollid", Scale = 2 } });
                var made = graph.Nodes.TryGetValue("/Made", out var m) ? m : null;
                check("create reports a property that did not take, and sets the others",
                    created.Success && Reply(created).Contains("property_errors") && Reply(created).Contains("Mode") && made?.State.Scale == 2,
                    Reply(created));

                var forbidden = await Ask(locked, "execute", new { code = "return 1;" });
                var allowed = await Ask(handler, "execute", new { code = "return 1;" });
                check("execute is refused with FORBIDDEN when the plugin turns it off, and runs by default",
                    !forbidden.Success && forbidden.ErrorCode == "FORBIDDEN" && allowed.Success, Reply(forbidden) + " / " + Reply(allowed));

                var outside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, "escaped.cs");
                var up = await Ask(handler, "write_script", new { path = "../escaped.cs", content = "// x" });
                var absolute = await Ask(handler, "write_script", new { path = outside, content = "// x" });
                check("write_script refuses a path that leaves the project, relative or absolute, and writes nothing",
                    !up.Success && !absolute.Success && Reply(up).Contains("outside the project") && !System.IO.File.Exists(outside),
                    Reply(up) + " / " + Reply(absolute));
                var inside = await Ask(handler, "write_script", new { path = "Assets/Scripts/Ok.cs", content = "// ok" });
                check("write_script still writes inside the project",
                    inside.Success && System.IO.File.Exists(System.IO.Path.Combine(project, "Assets", "Scripts", "Ok.cs")), Reply(inside));

                var big = Reply(await Ask(handler, "inspect", new { target = "/Big" }));
                bool valid;
                int total = -1;
                bool truncated = false;
                try
                {
                    using var parsed = JsonDocument.Parse(big);
                    truncated = parsed.RootElement.TryGetProperty("truncated", out var t) && t.GetBoolean();
                    total = parsed.RootElement.TryGetProperty("totalProperties", out var tp) ? tp.GetInt32() : -1;
                    valid = true;
                }
                catch (JsonException) { valid = false; }
                check("an inspection over the cap is still valid JSON, marked truncated with the totals",
                    valid && truncated && total == 3000 && big.Length <= 30_000, $"{big.Length} chars, valid {valid}, total {total}");

                var info = await Ask(handler, "get_project_info", new { });
                check("get_project_info reads the editor on the main thread", info.Success && Reply(info).Contains("Assets/Main.unity"), Reply(info));
            }
            finally
            {
                try { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(project)!, true); } catch { /* temp */ }
            }
        }

        private static async Task<IpcResponse> Ask(IpcRequestHandler handler, string method, object args)
            => await handler.HandleRequest(new IpcRequest { Id = 1, Method = method, Payload = JsonSerializer.SerializeToUtf8Bytes(args) },
                CancellationToken.None);

        private static string Reply(IpcResponse response) =>
            response.Success ? Encoding.UTF8.GetString(response.Payload ?? Array.Empty<byte>()) : response.Error ?? "";

        private static JsonElement J(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static string Fails(Action action) => Fails(action, out _);

        /// <summary>The exception's message, or "(no error)" when the action went through.</summary>
        private static string Fails(Action action, out Exception? error)
        {
            try { action(); error = null; return "(no error)"; }
            catch (Exception ex) { error = ex; return ex.Message; }
        }
    }

    /// <summary>Overloads shaped like Rigidbody.AddForce, Animator.Play, GameObject.SetActive and Godot's Node.GetNode.</summary>
    public sealed class VmBody
    {
        public string Last = "";
        public void AddForce(FakeVector force) => Last = $"vec {force.x:0},{force.y:0},{force.z:0}";
        public void AddForce(FakeVector force, FakeMode mode) => Last = $"vec {force.x:0},{force.y:0},{force.z:0} {mode}";
        public void AddForce(float x, float y, float z) => Last = $"xyz {x:0},{y:0},{z:0}";
        public void SetActive(bool value) => Last = $"active {value}";
        // Declared first so that a choice by declaration order, not by fit, picks it for a number.
        public void Play(string stateName, int layer = -1) => Last = $"name {stateName} {layer}";
        public void Play(int stateHash, int layer = -1) => Last = $"hash {stateHash} {layer}";
        public void Log(string text) => Last = "log text " + text;
        public void Log(FakeVector vector) => Last = $"log vector {vector.y:0}";
        public T GetNode<T>(string path) where T : class => throw new InvalidOperationException("the generic one ran");
        public string GetNode(string path) => Last = $"node {path}";
        public void Warp(FakeVector to) => throw new InvalidOperationException("the agent is not on a NavMesh");
        public void Sum(params int[] values) => Last = $"sum {values.Sum()}";
        public void Bus(VmName name) => Last = $"bus {name}";
        public void Scale(float factor) => Last = "scale " + factor.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A target with the same method name and no overload the arguments fit, as a Transform beside a Rigidbody.</summary>
    public sealed class VmOther
    {
        public int Calls;
        public void AddForce(string reason) => Calls++;
    }

    /// <summary>Shaped like Godot's StringName: a class made from a string through an implicit operator.</summary>
    public sealed class VmName
    {
        private readonly string _text;
        private VmName(string text) => _text = text;
        public static implicit operator VmName(string text) => new VmName(text);
        public override string ToString() => _text;
    }

    /// <summary>Stands for UnityEngine.Object / GodotObject: registered as reference-only.</summary>
    public class VmEngineObject { }

    public sealed class VmEngineNode : VmEngineObject
    {
        public static int Built;
        public VmEngineNode() => Built++;
        public string Label { get; set; } = "";
    }

    public sealed class VmHolder
    {
        public FakeVector Position { get; set; } = new FakeVector(1, 2, 3);
        public string Label { get; set; } = "holder";
    }

    public class VmBase { public int Size { get; set; } }

    public sealed class VmDerived : VmBase { public new float Size { get; set; } }

    /// <summary>Shaped like Unity's Projector: two properties whose names differ only in case, one obsolete.</summary>
    public sealed class VmProjector
    {
        public float orthographicSize { get; set; }
        [Obsolete("use orthographicSize")] public float orthoGraphicSize { get; set; }
    }

    /// <summary>The members a component holds: arrays, lists, a dictionary, structs inside collections, a guarded setter.</summary>
    public sealed class VmState
    {
        public int[] Numbers = { 1, 2, 3 };
        public List<FakeMode> Modes = new List<FakeMode> { FakeMode.Off };
        public Dictionary<string, float> Weights = new Dictionary<string, float>();
        public FakeVector[] PointArray = new FakeVector[2];
        public List<FakeVector> PointList = new List<FakeVector> { new FakeVector(), new FakeVector() };
        public List<FakeVector> Waypoints = new List<FakeVector>();
        public FakeAsset? Asset = new FakeAsset();
        public FakeMode Mode;
        public float Scale;
        public int Guarded { get => 0; set => throw new ArgumentException("the setter refused it"); }
    }

    /// <summary>
    /// A node whose UnderlyingObject declares none of the members it writes — as a Unity GameObject has no "position" and
    /// no component's list — so the handler cannot type a value and the node's resolver does.
    /// </summary>
    public sealed class VmNode : ISceneNode
    {
        private static readonly PropertyPathResolver Resolver = new PropertyPathResolver();
        private static readonly MethodInvoker Invoker = new MethodInvoker();
        private readonly int _extraProperties;

        public VmNode(string path, int extraProperties = 0)
        {
            Path = path;
            _extraProperties = extraProperties;
        }

        public readonly VmState State = new VmState();
        public readonly VmBody Body = new VmBody();

        public string Name => Path.TrimStart('/');
        public string Path { get; }
        public string TypeName => "VmNode";
        public ISceneNode? Parent => null;
        public IEnumerable<ISceneNode> Children => Array.Empty<ISceneNode>();
        public object UnderlyingObject { get; } = new object();

        public object? GetProperty(string propertyPath) => Resolver.Resolve(State, propertyPath);
        public void SetProperty(string propertyPath, object? value) => Resolver.Set(State, propertyPath, value);
        public object? CallMethod(string methodName, object?[]? args) => Invoker.Invoke(new object[] { State, Body }, methodName, args);
        public IEnumerable<ComponentInfo> GetComponents() => Array.Empty<ComponentInfo>();

        // Values with quotes and a backslash: a reply cut in the middle of one is not JSON.
        public IEnumerable<PropertyDescriptor> GetProperties()
        {
            for (int i = 0; i < _extraProperties; i++)
                yield return new PropertyDescriptor { Name = $"property{i:D4}", TypeName = "string", CanRead = true, Value = "a \"quoted\" value \\ of some length" };
        }

        public IEnumerable<MethodDescriptor> GetMethods() => Array.Empty<MethodDescriptor>();
    }

    public sealed class VmGraph : ISceneGraph
    {
        public readonly Dictionary<string, VmNode> Nodes = new Dictionary<string, VmNode>();

        public VmNode Add(VmNode node)
        {
            Nodes[node.Path] = node;
            return node;
        }

        public ISceneNode? GetNode(string path) => Nodes.TryGetValue(path, out var node) ? node : null;
        public IEnumerable<ISceneNode> GetRootNodes() => Nodes.Values;
        public IEnumerable<ISceneNode> Query(QueryFilter filter) => Nodes.Values;
        public ISceneNode CreateNode(string type, string? name, string? parentPath) => Add(new VmNode("/" + (name ?? type)));
        public bool DeleteNode(string path, bool recursive = true) => Nodes.Remove(path);
        public int GetTotalNodeCount() => Nodes.Count;
    }

    /// <summary>Runs the action inline and says, while it runs, that this is the main thread.</summary>
    public sealed class VmDispatcher : IMainThreadDispatcher
    {
        [ThreadStatic] public static bool OnMainThread;

        public Task<T> RunOnMainThread<T>(Func<T> action, CancellationToken ct = default)
        {
            var was = OnMainThread;
            OnMainThread = true;
            try { return Task.FromResult(action()); }
            catch (Exception e) { return Task.FromException<T>(e); }
            finally { OnMainThread = was; }
        }

        public Task RunOnMainThread(Action action, CancellationToken ct = default)
            => RunOnMainThread(() => { action(); return true; }, ct);
    }

    /// <summary>An editor whose scene query throws off the main thread, as Unity's SceneManager.GetActiveScene does.</summary>
    public sealed class VmEditor : IEditorContext
    {
        public string ProjectPath = "";
        public bool IsEditorMode => true;
        public string? GetSelectedObjectPath() => null;
        public void SetSelection(string objectPath) { }
        public string? GetCurrentScenePath() => VmDispatcher.OnMainThread
            ? "Assets/Main.unity"
            : throw new InvalidOperationException("GetActiveScene can only be called from the main thread.");
        public void OpenScene(string path) { }
        public void SaveScene() { }
        public string GetProjectPath() => ProjectPath;
        public IEnumerable<LogEntry> GetRecentLogs(int count = 50) => Array.Empty<LogEntry>();
        public void Log(string message, LogLevel level = LogLevel.Info) { }
    }
}
