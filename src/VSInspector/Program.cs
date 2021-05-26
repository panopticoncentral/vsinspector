using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;

using EtwTools;

static void Log(string message) => Console.WriteLine($"[{DateTime.Now}] {message}");

var rootCommand = new RootCommand("Inspects Visual Studio processses.")
{
};
rootCommand.Handler = CommandHandler.Create(Run);

static void Run()
{
    HashSet<uint> processes = new();

    Log("Starting VSInspector.");

    Log("Creating ETW session.");
    var session = EtwSession.CreateSession("VSInspector", new()
    {
        LogFileMode = EtwLogFileMode.RealTime | EtwLogFileMode.SystemLogger,
        SystemTraceProvidersEnabled = EtwSystemTraceProvider.Process
    });

    Console.CancelKeyPress += (s, e) =>
    {
        session.Stop();
        e.Cancel = true;
    };

    Log("Starting log processing.");
    var eventCount = 0;
    var trace = new EtwTrace("VSInspector", true);
    _ = trace.Open(null, e =>
    {
        if (e.Provider == KernelProcessProvider.Id)
        {
            switch (e.Descriptor.Opcode)
            {
                case EtwEventOpcode.Start:
                    var startEvent = (KernelProcessProvider.StartEventV4)e;
                    if (startEvent.Data.ImageFileName == "devenv.exe")
                    {
                        var processId = startEvent.Data.ProcessId;
                        Console.WriteLine($"Process {processId} started.");
                        if (processes.Contains(processId))
                        {
                            Console.WriteLine($"Process {processId} already started.");
                        }
                        processes.Add(processId);
                    }
                    break;

                case EtwEventOpcode.End:
                    var endEvent = (KernelProcessProvider.EndEventV4)e;
                    if (endEvent.Data.ImageFileName == "devenv.exe")
                    {
                        var processId = endEvent.Data.ProcessId;
                        Console.WriteLine($"Process {processId} ended.");
                        if (!processes.Contains(processId))
                        {
                            Console.WriteLine($"Process {processId} not started.");
                        }
                        processes.Add(processId);
                    }
                    break;

                case EtwEventOpcode.DataCollectionStart:
                    var dcStartEvent = (KernelProcessProvider.DCStartEventV4)e;
                    if (dcStartEvent.Data.ImageFileName == "devenv.exe")
                    {
                        var processId = dcStartEvent.Data.ProcessId;
                        Console.WriteLine($"Process rundown {processId} already running.");
                        if (processes.Contains(processId))
                        {
                            Console.WriteLine($"Process {processId} already started.");
                        }
                        processes.Add(processId);
                    }
                    break;

                case EtwEventOpcode.DataCollectionEnd:
                    var dcEndEvent = (KernelProcessProvider.DCEndEventV4)e;
                    if (dcEndEvent.Data.ImageFileName == "devenv.exe")
                    {
                        var processId = dcEndEvent.Data.ProcessId;
                        Console.WriteLine($"Process rundown {processId} already ended.");
                        if (!processes.Contains(processId))
                        {
                            Console.WriteLine($"Process {processId} already not started.");
                        }
                        processes.Add(processId);
                    }
                    break;
            }
        }
        eventCount++;
    });
    trace.Process();

    Log($"Processed {eventCount} events.");
    Log("Stopping VSInspector.");
}

return await rootCommand.InvokeAsync(args);
