﻿module internal Nessos.MBrace.Azure.Runtime.Worker

open System.Diagnostics
open System.Threading

open Nessos.MBrace.Azure.Runtime
open Nessos.MBrace.Azure.Runtime.Resources

/// <summary>
///     Initializes a worker loop. Worker polls task queue of supplied
///     runtime for available tasks and executes as appropriate.
/// </summary>
/// <param name="runtime">Runtime to subscribe to.</param>
/// <param name="maxConcurrentTasks">Maximum tasks to be executed concurrently by worker.</param>
/// <param name="logf">Logger.</param>
let initWorker (runtime : RuntimeState) 
               (maxConcurrentTasks : int)
               (logger : Common.StorageLogger) : Async<unit> = async {

    let currentTaskCount = ref 0
    let runTask procId deps t =
        let provider = RuntimeProvider.FromTask runtime procId deps t
        Task.RunAsync provider deps t

    let rec loop () = async {
        if !currentTaskCount >= maxConcurrentTasks then
            do! Async.Sleep 500
            return! loop ()
        else
            try
                let! tasks = runtime.DequeueBatch(maxConcurrentTasks - !currentTaskCount)
                if Array.isEmpty tasks then
                    do! Async.Sleep 500
                else
                    for (task, procId, dependencies) in tasks do
                        let _ = Interlocked.Increment currentTaskCount
                        let runTask () = async {
                            do! logger.AsyncLogf "Process %s\nStarting task %s of type '%O'." procId task.TaskId task.Type

                            //use hb = leaseMonitor.InitHeartBeat()

                            let sw = new Stopwatch()
                            sw.Start()
                            let! result = runTask procId dependencies task |> Async.Catch
                            sw.Stop()

                            match result with
                            | Choice1Of2 () -> 
                                //leaseMonitor.Release()
                                do! logger.AsyncLogf "Process %s\nTask %s completed after %O." procId task.TaskId sw.Elapsed
                                
                            | Choice2Of2 e -> 
                                //leaseMonitor.DeclareFault()
                                do! logger.AsyncLogf "Process %s\nTask %s faulted with:\n %O." procId task.TaskId e

                            let _ = Interlocked.Decrement currentTaskCount
                            return ()
                        }
        
                        let! handle = Async.StartChild(runTask())
                        ()
                    do! Async.Sleep 200
            with e -> 
                do! logger.AsyncLogf "WORKER FAULT: %O" e
                do! Async.Sleep 1000

            return! loop ()
    }

    return! loop ()
}
        