using System;
using System.Collections.Generic;

namespace AkerMcp.Server.Platform
{
    /// <summary>
    /// How a shortcut is written — "ctrl+shift+s", "alt+F4", "Escape" — taken apart into its
    /// modifiers and its key, the same way on every OS. Each injector then maps the names to
    /// its own codes; only the spelling is shared, so the two cannot drift apart again.
    ///
    /// <para>"+" separates the keys, so the plus key itself is written "ctrl++" or
    /// "ctrl+plus", and a lone "+" is that key alone. A chord that ends in a single "+" names
    /// no key at all and is refused: "ctrl+" used to be read as "ctrl" and the plus key, and
    /// went out as Ctrl+= on Windows and Cmd+= on macOS — a zoom where the caller had simply
    /// left the key out.</para>
    /// </summary>
    internal static class KeyChordSyntax
    {
        /// <summary>The name the parser gives the plus key; both injectors map it.</summary>
        public const string PlusKey = "plus";

        public static bool TryParse(string? chord, out List<string> modifiers, out string key, out string? error)
        {
            modifiers = new List<string>();
            key = "";
            error = null;
            if (string.IsNullOrWhiteSpace(chord))
            {
                error = "No key to send.";
                return false;
            }

            var parts = new List<string>(chord.Split('+'));
            for (int i = 0; i < parts.Count; i++) parts[i] = parts[i].Trim();

            if (parts[^1].Length == 0)
            {
                // The last part is empty when the chord ends in "+". Two empties in a row at the
                // end are "++": the plus key. One alone is a chord whose key is missing — unless
                // the "+" is the whole chord.
                bool lonePlus = parts.Count == 2 && parts[0].Length == 0;
                bool doublePlus = parts.Count >= 3 && parts[^2].Length == 0;
                if (!lonePlus && !doublePlus)
                {
                    error = $"'{chord.Trim()}' ends with '+' and names no key. Put the key last " +
                            "('ctrl+s'); for the plus key itself write 'ctrl++' or 'ctrl+plus'.";
                    return false;
                }
                parts.RemoveRange(parts.Count - 2, 2);
                parts.Add(PlusKey);
            }

            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Length > 0) continue;
                error = $"'{chord.Trim()}' has an empty name between two '+'. Write shortcuts as " +
                        "'ctrl+shift+s', with the modifiers first and the key last.";
                return false;
            }

            key = parts[^1];
            modifiers.AddRange(parts.GetRange(0, parts.Count - 1));
            return true;
        }
    }
}
