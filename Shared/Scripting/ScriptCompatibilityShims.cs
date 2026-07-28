#nullable enable

using System;

namespace AkerMcp.Shared.Scripting
{
    /// <summary>
    /// Types the C# compiler expects to find when certain language features are used, and that older
    /// runtimes simply do not ship.
    ///
    /// <para><b>Why the executor emits them.</b> `record` and `init`-only setters compile down to a
    /// reference to <c>System.Runtime.CompilerServices.IsExternalInit</c>, a marker type introduced with
    /// .NET 5. On a host still running Mono / netstandard2.1 the feature is available in the compiler but
    /// the marker is missing, so the snippet fails with an error about a type nobody wrote and nobody can
    /// import. Declaring the marker ourselves is the standard remedy — doing it once here spares every
    /// caller from pasting the same incantation into their snippet.</para>
    /// </summary>
    public static class ScriptCompatibilityShims
    {
        private const string IsExternalInitTypeName = "System.Runtime.CompilerServices.IsExternalInit";

        private const string IsExternalInitSource =
            "namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }";

        private static string? _cached;

        /// <summary>
        /// Source to append to a generated script file so that modern language features work on this host.
        /// Empty when the runtime already provides everything. Evaluated once: the answer cannot change
        /// without the assemblies (and therefore this class) being reloaded.
        /// </summary>
        public static string ForCurrentRuntime() => _cached ??= HasUsableType(IsExternalInitTypeName) ? string.Empty : IsExternalInitSource;

        // "Usable" means PUBLIC, and the distinction is not academic: assemblies compiled for older
        // runtimes routinely declare their own `internal` IsExternalInit to get records working. Such a
        // copy exists but cannot be referenced from generated code — treating it as "already provided"
        // would suppress the shim and leave the snippet failing on a type it can see and cannot touch.
        private static bool HasUsableType(string fullName)
        {
            var direct = Type.GetType(fullName, throwOnError: false);
            if (direct != null && direct.IsPublic) return true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null && type.IsPublic) return true;
                }
                catch
                {
                    // An assembly that refuses to be inspected simply doesn't answer the question.
                }
            }
            return false;
        }
    }
}
