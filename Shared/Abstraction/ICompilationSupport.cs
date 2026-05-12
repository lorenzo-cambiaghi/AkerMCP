using System.Collections.Generic;

namespace AkerMcp.Shared.Abstraction
{
    public interface ICompilationSupport
    {
        void RequestRecompile();
        CompilationStatus GetCompilationStatus();
        IEnumerable<CompileMessage> GetCompileMessages();
        void ClearCompileMessages();
    }

    public class CompilationStatus
    {
        public bool IsCompiling { get; set; }
        public bool LastCompileSucceeded { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public string LastCompileTime { get; set; } = null!;
    }

    public class CompileMessage
    {
        public CompileMessageType Type { get; set; }
        public string Message { get; set; } = null!;
        public string File { get; set; } = null!;
        public int Line { get; set; }
        public int Column { get; set; }
    }

    public enum CompileMessageType
    {
        Error,
        Warning
    }
}
