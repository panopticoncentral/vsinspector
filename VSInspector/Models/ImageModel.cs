using System;
using System.Collections.Generic;

namespace VSInspector.Models
{
    internal sealed class ImageModel
    {
        private readonly object _lock = new object();
        private readonly List<IImageObserver> _observers = new List<IImageObserver>();
        private ImageStatus _status;

        public string FileName { get; }

        public int LoadOrder { get; }

        public ulong ImageBase { get; }

        public ImageModel(string fileName, int loadOrder, ulong imageBase)
        {
            FileName = fileName;
            LoadOrder = loadOrder;
            ImageBase = imageBase;
            _status = ImageStatus.Loaded;
        }

        public void AddObserver(IImageObserver observer)
        {
            lock (_lock)
            {
                _observers.Add(observer);
                observer.NotifyStatusChanged(_status);
            }
        }

        public void RemoveObserver(IImageObserver observer)
        {
            lock (_lock)
            {
                _observers.Remove(observer);
            }
        }

        public void NotifyUnload()
        {
            lock (_lock)
            {
                _status = ImageStatus.Unloaded;
                _observers.ForEach(o => o.NotifyStatusChanged(_status));
            }
        }
    }
}
