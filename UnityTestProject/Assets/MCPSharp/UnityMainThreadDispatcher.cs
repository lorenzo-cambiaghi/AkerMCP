#if UNITY_EDITOR
using UnityEditor;
using MCPSharp.Client;

namespace MCPSharp.Unity
{
    public class UnityMainThreadDispatcher : MainThreadDispatcherBase
    {
        private bool _registered;

        protected override void ScheduleProcessQueue()
        {
            if (_registered) return;
            _registered = true;
            EditorApplication.update += OnEditorUpdate;
        }

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
#endif
