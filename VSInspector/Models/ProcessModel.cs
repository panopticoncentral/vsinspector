using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace VSInspector.Models
{
    internal sealed class ProcessModel
    {
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private const uint EVENT_SYSTEM_NAMECHANGE = 0x800C;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const string NoWindowTitle = "<No window>";

        private readonly object _lock = new object();
        private readonly WinEventDelegate _delegate;
        private readonly List<ImageModel> _images = new List<ImageModel>();
        private readonly Dictionary<ulong, ImageModel> _loadedImages = new Dictionary<ulong, ImageModel>();
        private readonly List<IProcessObserver> _observers = new List<IProcessObserver>();
        private IntPtr _hookHandle;
        private string _windowTitle;
        private ProcessStatus _status;

        public int ProcessId { get; }

        public ProcessModel(int processId)
        {
            ProcessId = processId;
            _delegate = WinEventProc;
            var process = Process.GetProcessById(processId);
            _windowTitle = process.MainWindowTitle;
            if (string.IsNullOrWhiteSpace(_windowTitle))
            {
                _windowTitle = NoWindowTitle;
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                _hookHandle = SetWinEventHook(EVENT_SYSTEM_NAMECHANGE, EVENT_SYSTEM_NAMECHANGE, IntPtr.Zero, _delegate, (uint)processId, 0, WINEVENT_OUTOFCONTEXT);
            });
        }

        public void AddObserver(IProcessObserver observer)
        {
            lock (_lock)
            {
                _observers.Add(observer);
                observer.NotifyWindowTitleChanged(_windowTitle);
                observer.NotifyStatusChanged(_status);
            }
        }

        public void NotifyEnded()
        {
            Unhook();

            lock (_lock)
            {
                _status = ProcessStatus.Ended;
                _windowTitle = NoWindowTitle;
                _observers.ForEach(o =>
                {
                    o.NotifyWindowTitleChanged(_windowTitle);
                    o.NotifyStatusChanged(_status);
                });
            }
        }

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            lock (_lock)
            {
                var mainWindow = Process.GetProcessById(ProcessId).MainWindowHandle;

                if (hwnd != mainWindow)
                {
                    return;
                }

                var process = Process.GetProcessById(ProcessId);
                _windowTitle = process.MainWindowTitle;
                _observers.ForEach(o => o.NotifyWindowTitleChanged(_windowTitle));
            }
        }

        private void Unhook()
        {
            if (_hookHandle == IntPtr.Zero)
            {
                return;
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                UnhookWinEvent(_hookHandle);
                _hookHandle = IntPtr.Zero;
            });
        }

        public void NotifyImageLoad(string fileName, ulong imageBase)
        {
            lock (_lock)
            {
                if (_loadedImages.ContainsKey(imageBase))
                {
                    return;
                }

                var imageModel = new ImageModel(fileName, _images.Count, imageBase);
                _images.Add(imageModel);
                _loadedImages[imageBase] = imageModel;
                _observers.ForEach(o => o.NotifyImageLoad(imageModel));
            }
        }

        public void NotifyImageUnload(string fileName, ulong imageBase)
        {
            lock (_lock)
            {
                if (!_loadedImages.TryGetValue(imageBase, out var imageModel) ||
                    imageModel.FileName != fileName)
                {
                    return;
                }

                _loadedImages.Remove(imageBase);
                imageModel.NotifyUnload();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                Unhook();
            }
        }
    }
}
