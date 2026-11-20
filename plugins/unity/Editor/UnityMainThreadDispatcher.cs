#nullable enable

using UnityEditor;
using AkerMcp.Client;

namespace AkerMcp.Unity
{
    public class UnityMainThreadDispatcher : MainThreadDispatcherBase
    {
        private bool _registered;

        /// <summary>
        /// Built by <c>EnginePluginBase.Start</c>, on Unity's main thread, and subscribed here once. It used to
        /// subscribe on the first request, from the pipe's background thread: `EditorApplication.update +=` is not an
        /// atomic update of the delegate, so a subscription lost to a concurrent editor change left the queue never
        /// pumped and every request timing out until the next domain reload.
        /// </summary>
        public UnityMainThreadDispatcher()
        {
            _registered = true;
            EditorApplication.update += OnEditorUpdate;
        }

        // The editor ticks the queue every update; there is nothing to schedule.
        protected override void ScheduleProcessQueue() { }

        private void OnEditorUpdate()
        {
            ProcessQueue();
        }

        public void Unregister()
        {
            if (!_registered) return;
            _registered = false;
            EditorApplication.update -= OnEditorUpdate;
        }
    }
}
