using System;
using System.Collections.Concurrent;
using System.IO;

namespace OmniSharp.FileWatching
{
    internal partial class ManualFileSystemWatcher : IFileSystemWatcher, IFileSystemNotifier
    {
        private readonly object _gate = new object();
        private readonly ConcurrentDictionary<string, Callbacks> _callbacksMap;
        private readonly Callbacks _folderCallbacks = new Callbacks();

        public ManualFileSystemWatcher()
        {
            _callbacksMap = new ConcurrentDictionary<string, Callbacks>(StringComparer.OrdinalIgnoreCase);
        }

        public void Notify(string filePath, FileChangeType changeType = FileChangeType.Unspecified)
        {
            lock (_gate)
            {
                if(changeType == FileChangeType.DirectoryDelete)
                {
                    _folderCallbacks.Invoke(filePath, FileChangeType.DirectoryDelete);
                }

                if (_callbacksMap.TryGetValue(filePath, out var fileCallbacks))
                {
                    fileCallbacks.Invoke(filePath, changeType);
                }

                var directoryPath = Path.GetDirectoryName(filePath);
                if (_callbacksMap.TryGetValue(directoryPath, out var directoryCallbacks))
                {
                    directoryCallbacks.Invoke(filePath, changeType);
                }

                var extension = Path.GetExtension(filePath);
                if (!string.IsNullOrEmpty(extension) &&
                    _callbacksMap.TryGetValue(extension, out var extensionCallbacks))
                {
                    extensionCallbacks.Invoke(filePath, changeType);
                }
            }
        }

        public void WatchDirectories(FileSystemNotificationCallback callback)
        {
            _folderCallbacks.Add(callback);
        }

        public void Watch(string pathOrExtension, FileSystemNotificationCallback callback)
        {
            if (pathOrExtension == null)
            {
                throw new ArgumentNullException(nameof(pathOrExtension));
            }

            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            if (!_callbacksMap.TryGetValue(pathOrExtension, out var callbacks))
            {
                callbacks = new Callbacks();
                _callbacksMap[pathOrExtension] = callbacks;
            }

            callbacks.Add(callback);
        }
    }
}
