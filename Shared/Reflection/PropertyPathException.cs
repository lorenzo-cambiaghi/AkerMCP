using System;

namespace AkerMcp.Shared.Reflection
{
    public class PropertyPathException : Exception
    {
        public PropertyPathException(string message) : base(message) { }
        public PropertyPathException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The path resolved and the member exists, but the value does not fit it. A node that tries a path on several
    /// components catches <see cref="PropertyPathException"/> to move on to the next one; this one it must let through,
    /// or "that is not a Vector3" turns into "not found on any component".
    /// </summary>
    public class PropertyValueException : PropertyPathException
    {
        public PropertyValueException(string message) : base(message) { }
        public PropertyValueException(string message, Exception inner) : base(message, inner) { }
    }
}
