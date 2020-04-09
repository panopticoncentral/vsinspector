﻿using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace VSInspector.Models
{
    internal sealed class InspectorModel
    {
        private const KernelTraceEventParser.Keywords KernelProviders =
            KernelTraceEventParser.Keywords.ImageLoad |
            KernelTraceEventParser.Keywords.Process;

        private const KernelTraceEventParser.Keywords KernelStackTrace =
            KernelTraceEventParser.Keywords.Process;

        private const ClrTraceEventParser.Keywords ClrProviders =
            ClrTraceEventParser.Keywords.Jit |
            ClrTraceEventParser.Keywords.JittedMethodILToNativeMap |
            ClrTraceEventParser.Keywords.Loader;

        private const ClrTraceEventParser.Keywords ClrRundownProviders =
            ClrProviders |
            ClrTraceEventParser.Keywords.StartEnumeration;

        private static InspectorModel _instance;

        private readonly object _lock = new object();
        private readonly List<IInspectorObserver> _observers = new List<IInspectorObserver>();
        private readonly Dictionary<int, ProcessModel> _processes = new Dictionary<int, ProcessModel>();
        private readonly TraceEventSession _eventSession;
        private readonly TraceLogEventSource _traceLogEventSource;

        public static InspectorModel Instance => _instance ?? (_instance = new InspectorModel());

        public InspectorModel()
        {
            _eventSession = new TraceEventSession("VSInspector");
            _eventSession.EnableKernelProvider(KernelProviders, KernelStackTrace);
            _eventSession.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Informational, (ulong)ClrProviders);
            _eventSession.EnableProvider(ClrRundownTraceEventParser.ProviderGuid, TraceEventLevel.Informational, (ulong)ClrRundownProviders);

            _traceLogEventSource = TraceLog.CreateFromTraceEventSession(_eventSession);
            _traceLogEventSource.Kernel.ProcessStartGroup += Kernel_ProcessStart;
            _traceLogEventSource.Kernel.ProcessStop += Kernel_ProcessStop;
            _traceLogEventSource.Kernel.ImageDCStart += Kernel_ImageDCStart;
            _traceLogEventSource.Kernel.ImageLoad += Kernel_ImageLoad;
            _traceLogEventSource.Kernel.ImageUnload += Kernel_ImageUnload;

            Task.Run(() => _traceLogEventSource.Process());
        }

        public void Dispose()
        {
            _traceLogEventSource.Kernel.ProcessStartGroup -= Kernel_ProcessStart;
            _traceLogEventSource.Kernel.ProcessStop -= Kernel_ProcessStop;
            _traceLogEventSource.Kernel.ImageDCStart -= Kernel_ImageDCStart;
            _traceLogEventSource.Kernel.ImageLoad -= Kernel_ImageLoad;
            _traceLogEventSource.Kernel.ImageUnload -= Kernel_ImageUnload;

            _traceLogEventSource.Dispose();
            _eventSession.Dispose();

            lock (_lock)
            {
                foreach (var processModel in _processes.Values)
                {
                    processModel.Dispose();
                }
            }
        }

        public void AddObserver(IInspectorObserver observer)
        {
            lock (_lock)
            {
                _observers.Add(observer);
                foreach (var process in _processes.Values)
                {
                    observer.NotifyProcessStart(process);
                }
            }
        }

        public void RemoveObserver(IInspectorObserver observer)
        {
            lock (_lock)
            {
                _observers.Remove(observer);
            }
        }

        private static bool EventFilter(TraceEvent e) =>
            e.ProcessName == "devenv";

        private void Kernel_ProcessStart(ProcessTraceData data)
        {
            if (!EventFilter(data))
            {
                return;
            }

            lock (_lock)
            {
                var processModel = new ProcessModel(data.ProcessID);
                _processes[data.ProcessID] = processModel;
                _observers.ForEach(o => o.NotifyProcessStart(processModel));
            }
        }

        private void Kernel_ProcessStop(ProcessTraceData data)
        {
            if (!EventFilter(data))
            {
                return;
            }

            lock (_lock)
            {
                if (_processes.TryGetValue(data.ProcessID, out var processModel))
                {
                    processModel.NotifyEnded();
                }
            }
        }

        private void Kernel_ImageDCStart(ImageLoadTraceData data)
        {
            if (!EventFilter(data))
            {
                return;
            }

            lock (_lock)
            {
                if (_processes.TryGetValue(data.ProcessID, out var processModel))
                {
                    processModel.NotifyImageLoad(data.FileName, data.ImageBase);
                }
            }
        }

        private void Kernel_ImageLoad(ImageLoadTraceData data)
        {
            if (!EventFilter(data))
            {
                return;
            }

            lock (_lock)
            {
                var symbolPath = new SymbolPath(SymbolPath.SymbolPathFromEnvironment).Add(SymbolPath.MicrosoftSymbolServerPath);
                SymbolReader symbolReader = new SymbolReader(new StringWriter(), symbolPath.ToString())
                {
                    SecurityCheck = path => true
                };
                var callStack = data.CallStack();
                if (callStack != null)
                {
                    while (callStack != null)
                    {
                        var codeAddress = callStack.CodeAddress;
                        if (codeAddress.Method == null)
                        {
                            var moduleFile = codeAddress.ModuleFile;
                            if (moduleFile == null)
                            {
                                Trace.WriteLine($"Could not find module for Address 0x{codeAddress.Address:x}");
                            }
                            else
                            {
                                codeAddress.CodeAddresses.LookupSymbolsForModule(symbolReader, moduleFile);
                            }
                        }
                        callStack = callStack.Caller;
                    }
                }

                if (_processes.TryGetValue(data.ProcessID, out var processModel))
                {
                    processModel.NotifyImageLoad(data.FileName, data.ImageBase);
                }
            }
        }

        private void Kernel_ImageUnload(ImageLoadTraceData data)
        {
            if (!EventFilter(data))
            {
                return;
            }

            lock (_lock)
            {
                if (_processes.TryGetValue(data.ProcessID, out var processModel))
                {
                    processModel.NotifyImageUnload(data.FileName, data.ImageBase);
                }
            }
        }
    }
}
