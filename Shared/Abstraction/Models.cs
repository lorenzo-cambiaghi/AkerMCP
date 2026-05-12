using System;
using System.Collections.Generic;

namespace AkerMcp.Shared.Abstraction
{
    public class QueryFilter
    {
        public string? TypeFilter { get; set; }
        public string? NamePattern { get; set; }
        public Dictionary<string, object?>? PropertyFilter { get; set; }
        public string? Tag { get; set; }
        public int MaxResults { get; set; } = 50;
    }

    public class PropertyDescriptor
    {
        public string Name { get; set; } = null!;
        public string TypeName { get; set; } = null!;
        public bool CanRead { get; set; }
        public bool CanWrite { get; set; }
        public object? Value { get; set; }
    }

    public class MethodDescriptor
    {
        public string Name { get; set; } = null!;
        public string ReturnType { get; set; } = null!;
        public List<ParameterDescriptor> Parameters { get; set; } = new();
    }

    public class ParameterDescriptor
    {
        public string Name { get; set; } = null!;
        public string TypeName { get; set; } = null!;
        public bool IsOptional { get; set; }
        public object? DefaultValue { get; set; }
    }

    public class AssetInfo
    {
        public string Path { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string TypeName { get; set; } = null!;
        public long SizeBytes { get; set; }
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; } = null!;
        public string? StackTrace { get; set; }
    }

    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public class InspectionResult
    {
        public string TypeName { get; set; } = null!;
        public string? Path { get; set; }
        public List<PropertyDescriptor> Properties { get; set; } = new();
        public List<MethodDescriptor>? Methods { get; set; }
        public List<string>? ChildNames { get; set; }
        public string? Summary { get; set; }
    }
}
