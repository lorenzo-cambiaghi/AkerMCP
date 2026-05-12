using System;
using System.Threading;
using System.Threading.Tasks;

namespace MCPSharp.Shared.Abstraction
{
    public interface IMainThreadDispatcher
    {
        Task<T> RunOnMainThread<T>(Func<T> action, CancellationToken ct = default);
        Task RunOnMainThread(Action action, CancellationToken ct = default);
    }
}
