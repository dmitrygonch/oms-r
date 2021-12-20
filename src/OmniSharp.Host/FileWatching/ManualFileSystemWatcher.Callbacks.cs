using System.Collections.Concurrent;

namespace OmniSharp.FileWatching
{
    internal partial class ManualFileSystemWatcher
    {
        private class Callbacks
        {
            private readonly ConcurrentDictionary<FileSystemNotificationCallback, byte> _callbacks = new();

            public void Add(FileSystemNotificationCallback callback)
            {
                _callbacks[callback] = 0;
            }

            public void Invoke(string filePath, FileChangeType changeType)
            {
                foreach (var callback in _callbacks)
                {
                    callback.Key(filePath, changeType);
                }
            }
        }
    }
}
