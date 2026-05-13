using System.Threading;
using System.Threading.Tasks;

namespace AkerMcp.Shared.Abstraction
{
    public interface ICodeExecutor
    {
        Task<CodeExecutionResult> Execute(string code, int timeoutMs = 5000, CancellationToken ct = default);
    }

    public class CodeExecutionResult
    {
        public bool Success { get; set; }
        public string? ReturnValue { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
        public double ElapsedMs { get; set; }
    }
}
