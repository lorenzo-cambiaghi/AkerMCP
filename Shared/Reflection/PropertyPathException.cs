using System;

namespace MCPSharp.Shared.Reflection
{
    public class PropertyPathException : Exception
    {
        public PropertyPathException(string message) : base(message) { }
        public PropertyPathException(string message, Exception inner) : base(message, inner) { }
    }
}
