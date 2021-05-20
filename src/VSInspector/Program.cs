using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

// TODO: clr!WKS::GCHeap::WaitUntilGCComplete
// TODO: clr!JIT_New

static void Log(string message) => Console.WriteLine($"[{DateTime.Now}] {message}");

var rootCommand = new RootCommand("Inspects traces of Visual Studio processses.")
{
    new Option<string>(new[] { "--trace", "-t" }, "The trace file to process.") { IsRequired = true },
    new Option<bool>(new[] { "--log", "-l" }, "Save log messages."),
    new Option<bool>(new[] { "--cached-symbols-only", "-c" }, "Only use locally cached symbols." )
};

bool UnpackZip(string trace, bool log)
{
    string zipPath = null;
    if (Path.GetExtension(trace) == ".zip")
    {
        zipPath = trace;
        trace = Path.Combine(Path.GetDirectoryName(trace), Path.GetFileNameWithoutExtension(trace));
    }

    if (!File.Exists(trace))
    {
        if (zipPath != null)
        {
            Log("Unpacking the ZIP.");

            TextWriter zipLog = log ? File.CreateText("ziplog.txt") : null;
            ZippedETLReader zipReader = new(trace, zipLog);
            zipReader.UnpackArchive();
            zipLog?.Dispose();
        }
        else
        {
            Log($"Cannot find trace '{trace}'.");
            return false;
        }
    }
    else
    {
        Log("Already unpacked ZIP.");
    }

    return true;
}

TraceLog ConvertLog(string trace, bool log)
{
    Log("Opening the ETLX file.");
    TextWriter conversionLog = log ? File.CreateText("conversionlog.txt") : null;
    var traceLog = TraceLog.OpenOrConvert(trace.Substring(0, trace.LastIndexOf(".", StringComparison.Ordinal)), new TraceLogOptions { ConversionLog = conversionLog });
    conversionLog?.Dispose();
    return traceLog;
}

void PreloadSymbols(TraceLog traceLog, TraceProcess devenvProcess, bool cachedSymbolsOnly)
{
    Log("Pre-loading symbols.");
    var symbolLog = new StringWriter();
    var symbolReader = new SymbolReader(symbolLog)
    {
        Options = cachedSymbolsOnly ? SymbolReaderOptions.CacheOnly : SymbolReaderOptions.None,
        SecurityCheck = pdbPath => true
    };

    foreach (var m in devenvProcess.LoadedModules)
    {
        traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, m.ModuleFile);
    }
}

TraceThread FindMainThread(TraceProcess devenvProcess)
{
    foreach (var e in devenvProcess.EventsInProcess.ByEventType<SampledProfileTraceData>())
    {
        var callStack = e.CallStack();

        while (callStack != null)
        {
            var method = callStack.CodeAddress.Method;
            var module = callStack.CodeAddress.ModuleFile;

            if (module?.Name == "devenv" && method?.FullMethodName == "WinMain")
            {
                Log($"Found main thread {e.ThreadID}.");
                return e.Thread();
            }

            callStack = callStack.Caller;
        }
    }

    Log($"Couldn't find main thread!");
    return null;
}

var threadWaitReasonToString = new[] {
    "Executive",
    "FreePage",
    "PageIn",
    "PoolAllocation",
    "DelayExecution",
    "Suspended",
    "UserRequest",
    "WrExecutive",
    "WrFreePage",
    "WrPageIn",
    "WrPoolAllocation",
    "WrDelayExecution",
    "WrSuspended",
    "WrUserRequest",
    "WrEventPair",
    "WrQueue",
    "WrLpcReceive",
    "WrLpcReply",
    "WrVirtualMemory",
    "WrPageOut",
    "WrRendezvous",
    "WrKeyedEvent",
    "WrTerminated",
    "WrProcessInSwap",
    "WrCpuRateControl",
    "WrCalloutStack",
    "WrKernel",
    "WrResource",
    "WrPushLock",
    "WrMutex",
    "WrQuantumEnd",
    "WrDispatchInt",
    "WrPreempted",
    "WrYieldExecution",
    "WrFastMutex",
    "WrGuardedMutex",
    "WrRundown",
    "MaximumWaitReason"
};

int Run(string trace, bool log, bool cachedSymbolsOnly)
{
    if (!UnpackZip(trace, log))
    {
        return -1;
    }

    using var traceLog = ConvertLog(trace, log);

    if (traceLog.Processes.Where(p => p.Name == "devenv").Count() != 1)
    {
        Log("Expected only one devenv.exe process in trace.");
        return -1;
    }
    var devenvProcess = traceLog.Processes.FirstProcessWithName("devenv");

    PreloadSymbols(traceLog, devenvProcess, cachedSymbolsOnly);

    var thread = FindMainThread(devenvProcess);

    if (thread == null)
    {
        return -1;
    }

    Log("Processing callstacks.");
    Stack<string> stack = new();
    var start = 0.0;
    var end = 0.0;

    var skippedSamples = 0;
    var skippedContextSwitches = 0;

    var sampleCount = 0;
    var contextSwitchCount = 0;
    var readyThreadCount = 0;

    double lastContextSwitchOutTime = -1;
    double lastContextSwitchInTime = -1;
    var lastContextSwitchWasBlocking = false;
    double lastReadyTime = -1;
    var seenFirstContextSwitch = false;

    double runningTime = 0;
    double messagePumpWaitTime = 0;
    double blockedTime = 0;
    double notRunningTime = 0;
    double readyTime = 0;
    double processMessageTime = 0;
    double idleTime = 0;

    foreach (var e in traceLog.Events)
    {
        string CaptureCallstack()
        {
            end = e.TimeStampRelativeMSec;

            var callStack = e.CallStack();
            stack.Clear();

            while (callStack != null)
            {
                var method = callStack.CodeAddress.Method;
                var module = callStack.CodeAddress.ModuleFile;
                var frame = $"{(module != null ? module.Name : "?")}!{(method != null ? method.FullMethodName : callStack.CodeAddress.Address.ToString("x"))}";

                if (frame == "msenv!CMsoCMHandler::EnvironmentMsgLoop")
                {
                    // We found the bottom of the usual stack top:
                    // ntdll!_RtlUserThreadStart
                    // ntdll!__RtlUserThreadStart
                    // kernel32!BaseThreadInitThunk
                    // devenv!__scrt_common_main_seh
                    // devenv!WinMain
                    // devenv!CDevEnvAppId::Run
                    // devenv!util_CallVsMain
                    // msenv!VStudioMain
                    // msenv!VStudioMainLogged
                    // msenv!CMsoComponent::PushMsgLoop
                    // msenv!SCM_MsoCompMgr::FPushMessageLoop
                    // msenv!SCM::FPushMessageLoop
                    // msenv!CMsoCMHandler::FPushMessageLoop
                    // msenv!CMsoCMHandler::EnvironmentMsgLoop
                    break;
                }

                stack.Push(frame);
                callStack = callStack.Caller;
            }

            return (callStack == null || stack.Count == 0) ? null : stack.Peek();
        }

        switch (e)
        {
            case SampledProfileTraceData sample:
                if (thread.ThreadID != e.ThreadID)
                {
                    continue;
                }

                sampleCount++;

                if (CaptureCallstack() == null)
                {
                    skippedSamples++;
                    continue;
                }

                //Log($"{e.TimeStampRelativeMSec:N3} Sample (Processor={sample.ProcessorNumber} Priority={sample.Priority})");
                break;

            case CSwitchTraceData contextSwitch:
                if (contextSwitch.OldThreadID == thread.ThreadID)
                {
                    if (lastContextSwitchOutTime != -1)
                    {
                        Log($"Unexpected context switch out when already switched out.");
                    }

                    if (lastReadyTime != -1)
                    {
                        Log("Unexpected context switch out when already ready.");
                    }

                    if (seenFirstContextSwitch && lastContextSwitchInTime == -1)
                    {
                        Log("Unexpected context switch out with no switch in.");
                    }

                    contextSwitchCount++;
                    //Log($"{e.TimeStampRelativeMSec:N3} Switch out (Priority={contextSwitch.OldThreadPriority} Reason={threadWaitReasonToString[(int)contextSwitch.OldThreadWaitReason]})");

                    runningTime += e.TimeStampRelativeMSec - lastContextSwitchInTime;
                    lastContextSwitchInTime = -1;
                    lastContextSwitchOutTime = e.TimeStampRelativeMSec;
                    switch ((int)contextSwitch.OldThreadWaitReason)
                    {
                        // Executive, DelayExecution, Suspended, WrQuantumEnd, WrDispatchInt, WrPreempted
                        case 0 or 4 or 5 or (>= 30 and <= 32):
                            lastContextSwitchWasBlocking = false;
                            break;

                        // UserRequest, WrLpcReply, WrResource, WrPushLock, WrYieldExecution, WrFastMutex
                        case 6 or 17 or 27 or 28 or 33 or 34:
                            lastContextSwitchWasBlocking = true;
                            break;

                        default:
                            lastContextSwitchWasBlocking = false;
                            Log($"Unexpected context switch reason: {contextSwitch.OldThreadWaitReason}.");
                            break;
                    }
                }
                else if (contextSwitch.NewThreadID == thread.ThreadID)
                {
                    if (lastContextSwitchInTime != -1)
                    {
                        Log($"Unexpected context switch in when already switched in.");
                    }

                    if (seenFirstContextSwitch && lastContextSwitchOutTime == -1)
                    {
                        Log("Unexpected context switch in with no switch out.");
                    }

                    contextSwitchCount++;
                    //Log($"{e.TimeStampRelativeMSec:N3} Switch in (Processor={contextSwitch.ProcessorNumber} Priority={contextSwitch.NewThreadPriority})");

                    var stackTop = CaptureCallstack();
                    if (stackTop == null)
                    {
                        skippedContextSwitches++;
                    }
                    else
                    {
                        if (lastContextSwitchWasBlocking)
                        {
                            switch (stackTop)
                            {
                                case "vslog!VSResponsiveness::Detours::DetourMsgWaitForMultipleObjectsEx":
                                case "vslog!VSResponsiveness::Detours::DetourPeekMessageW":
                                    messagePumpWaitTime += e.TimeStampRelativeMSec - lastContextSwitchOutTime;
                                    break;

                                case "msenv!MainMessageLoop::ProcessMessage":
                                    processMessageTime += e.TimeStampRelativeMSec - lastContextSwitchOutTime;
                                    break;

                                case "msenv!MainMessageLoop::DoIdle":
                                    idleTime += e.TimeStampRelativeMSec - lastContextSwitchOutTime;
                                    break;

                                default:
                                    blockedTime += e.TimeStampRelativeMSec - lastContextSwitchOutTime;
                                    break;
                            }
                        }
                        else
                        {
                            notRunningTime += e.TimeStampRelativeMSec - lastContextSwitchOutTime;
                        }

                        if (lastReadyTime != -1)
                        {
                            readyTime += e.TimeStampRelativeMSec - lastReadyTime;
                        }
                    }

                    lastContextSwitchInTime = e.TimeStampRelativeMSec;
                    lastContextSwitchOutTime = -1;
                    lastReadyTime = -1;
                }
                else
                {
                    continue;
                }

                seenFirstContextSwitch = true;
                break;

            case DispatcherReadyThreadTraceData readyThread:
                if (readyThread.AwakenedThreadID != thread.ThreadID)
                {
                    continue;
                }

                readyThreadCount++;
                //Log($"{e.TimeStampRelativeMSec:N3} Ready (Reason={readyThread.AdjustReason} Increment={readyThread.AdjustIncrement} Flags={readyThread.Flags} Process={readyThread.ProcessName})");

                if (lastReadyTime != -1)
                {
                    Log("Unexpected reading of a ready thread.");
                }

                if (lastContextSwitchOutTime != -1)
                {
                    lastReadyTime = e.TimeStampRelativeMSec;
                }
                break;

            default:
                continue;
        }
    }

    Log($"{sampleCount:N0} samples, {contextSwitchCount:N0} context switches, {readyThreadCount:N0} ready thread.");
    Log($"Skipped {skippedSamples:N0} samples, {skippedContextSwitches:N0} context switches.");
    Log($"Spent {TimeSpan.FromMilliseconds(runningTime)} running, {TimeSpan.FromMilliseconds(messagePumpWaitTime)} in message pump, {TimeSpan.FromMilliseconds(processMessageTime)} processing messages, {TimeSpan.FromMilliseconds(idleTime)} idle processing, {TimeSpan.FromMilliseconds(blockedTime)} blocked, {TimeSpan.FromMilliseconds(notRunningTime)} not running, {TimeSpan.FromMilliseconds(readyTime)} ready.");
    Log($"Start {start:N0}, elapsed {TimeSpan.FromMilliseconds(end - start)} ({end - start:N0}).");

    return 0;
}

rootCommand.Handler = CommandHandler.Create<string, bool, bool>(Run);

return await rootCommand.InvokeAsync(args);
