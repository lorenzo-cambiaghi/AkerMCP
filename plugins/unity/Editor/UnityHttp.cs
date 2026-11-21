#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.Networking;

namespace AkerMcp.Unity.Editor
{
    /// <summary>
    /// HTTP for the editor window, through UnityWebRequest. The HttpClient of Unity's Mono fails GitHub's certificate
    /// chain (TrustFailure, "Chain instance is empty", Unity 6000.6.5 on 2026-11-21); UnityWebRequest uses Unity's own
    /// TLS and the system certificate store.
    ///
    /// Callable from any thread: the request is started on the main thread and watched from EditorApplication.update,
    /// which ticks even when the editor is not focused. Results come back on the thread pool.
    /// </summary>
    public static class UnityHttp
    {
        private const string UserAgent = "AkerMcp-Unity/" + McpEditorWindow.PluginVersion;

        /// <summary>Matches <see cref="AkerMcp.Shared.Setup.DownloadFile"/>: hand it to ServerInstall.EnsureAsync.</summary>
        public static Task DownloadFile(string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellation)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorMainThread.Post(() =>
            {
                var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET)
                {
                    downloadHandler = new DownloadHandlerFile(destinationPath) { removeFileOnAbort = true },
                    redirectLimit = 10,
                    timeout = 900,
                };
                Watch(request, progress, cancellation, () => done.TrySetResult(true), error => done.TrySetException(error));
            });
            return done.Task;
        }

        public static Task<string> GetText(string url, int timeoutSeconds = 15)
        {
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorMainThread.Post(() =>
            {
                var request = UnityWebRequest.Get(url);
                request.timeout = timeoutSeconds;
                var handler = request.downloadHandler;
                Watch(request, null, CancellationToken.None, () => done.TrySetResult(handler.text), error => done.TrySetException(error));
            });
            return done.Task;
        }

        private static void Watch(UnityWebRequest request, IProgress<double>? progress, CancellationToken cancellation,
            Action onSuccess, Action<Exception> onError)
        {
            request.SetRequestHeader("User-Agent", UserAgent);
            request.SendWebRequest();

            EditorApplication.CallbackFunction? tick = null;
            tick = () =>
            {
                if (cancellation.IsCancellationRequested && !request.isDone) request.Abort();
                progress?.Report(request.downloadProgress);
                if (!request.isDone) return;

                EditorApplication.update -= tick;
                try
                {
                    if (request.result == UnityWebRequest.Result.Success) onSuccess();
                    else if (cancellation.IsCancellationRequested) onError(new OperationCanceledException(cancellation));
                    else onError(new InvalidOperationException(
                        request.responseCode > 0
                            ? $"HTTP {request.responseCode} for {request.url}"
                            : $"{request.error} ({request.url})"));
                }
                finally
                {
                    request.Dispose();
                }
            };
            EditorApplication.update += tick;
        }
    }

    /// <summary>
    /// Runs work on the editor's main thread from anywhere. Drained on EditorApplication.update, which ticks even when
    /// the editor is not focused (delayCall waits for a repaint an unfocused editor never does). Hooked at every domain
    /// load, from the main thread, so Post only has to enqueue.
    /// </summary>
    public static class EditorMainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        public static void Post(Action action) => Queue.Enqueue(action);

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.update -= Drain;
            EditorApplication.update += Drain;
        }

        private static void Drain()
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
            }
        }
    }
}
