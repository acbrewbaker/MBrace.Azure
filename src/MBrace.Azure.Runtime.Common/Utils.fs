﻿namespace MBrace.Azure.Runtime.Common

    open System
    open System.Threading.Tasks
    open Microsoft.WindowsAzure.Storage.Table

    [<AutoOpen>]
    module Utils =

        let guid() = Guid.NewGuid().ToString("N")
        let uri fmt = Printf.ksprintf (fun s -> new Uri(s)) fmt

        type Async with
            static member Cast<'U>(task : Async<obj>) = async { let! t = task in return box t :?> 'U }
            static member Sleep(timespan : TimeSpan) = Async.Sleep(int timespan.TotalMilliseconds)
            static member AwaitTask(task : Task) = Async.AwaitTask(task.ContinueWith ignore)

        type AsyncBuilder with
            member __.Bind(f : Task<'T>, g : 'T -> Async<'S>) : Async<'S> = 
                __.Bind(Async.AwaitTask f, g)
            member __.Bind(f : Task, g : unit -> Async<'S>) : Async<'S> =
                __.Bind(Async.AwaitTask(f.ContinueWith ignore), g)
            member __.ReturnFrom(f : Task<'T>) : Async<'T> =
                __.ReturnFrom(Async.AwaitTask f)
            member __.ReturnFrom(f : Task) : Async<unit> =
                __.ReturnFrom(Async.AwaitTask f)

        type Uri with
            member u.ResourceId = u.Scheme
            member u.PartitionWithScheme = sprintf "%s:%s" u.Scheme u.PartitionKey
            member u.FileWithScheme = sprintf "%s:%s" u.Scheme u.File

            // Primary
            member u.Container = 
                let s = u.Segments.[0] in if s.EndsWith("/") then s.Substring(0, s.Length-1) else s
            member u.Table = u.Container
            member u.Queue = u.Container
        
            // Secondary
            member u.File = u.Segments.[1]
            member u.PartitionKey = u.File

            // Unique
            member u.RowKey = u.Segments.[2]

    module Storage =
        open MBrace.Azure.Runtime

        let processIdToStorageId (pid : string) = 
            sprintf "process%s" <| Guid.Parse(pid).ToString("N").Substring(0,7) // TODO : change

    type Live<'T>(provider : unit -> Async<'T>, initial : Choice<'T,exn>, ?keepLast : bool, ?interval : int, ?stopf : Choice<'T, exn> -> bool) =
        let interval = defaultArg interval 500
        let keepLast = defaultArg keepLast false
        let stopf = defaultArg stopf (fun _ -> false)
        let mutable value = initial

        let runOnce () = async {
            let! choice = Async.Catch <| provider ()
            match choice with
            | Choice1Of2 _ as v -> value <- v
            | Choice2Of2 _ when keepLast -> ()
            | Choice2Of2 _ as v -> value <- v
            return choice
        }

        let rec update () = async {
            let! choice = runOnce()
            if stopf choice then 
                return ()
            else
                do! Async.Sleep interval
                return! update ()
        }

        do runOnce() |> Async.Ignore |> Async.RunSynchronously
           update () |> Async.Start

        member __.TryGetValue () = 
            match value with
            | Choice1Of2 v -> Some v
            | Choice2Of2 _ -> None

        member __.Value =
            match value with
            | Choice1Of2 v -> v
            | Choice2Of2 e -> raise e


    [<RequireQualifiedAccess>]
    module Convert =
        
        open System.Text
        open System.IO
        open System.Collections.Generic

        // taken from : http://www.atrevido.net/blog/PermaLink.aspx?guid=debdd47c-9d15-4a2f-a796-99b0449aa8af
        let private encodingIndex = "qaz2wsx3edc4rfv5tgb6yhn7ujm8k9lp"
        let private inverseIndex = encodingIndex |> Seq.mapi (fun i c -> c,i) |> dict

        /// convert bytes to base-32 string: useful for file names in case-insensitive file systems
        let toBase32String(bytes : byte []) =
            let b = new StringBuilder()
            let mutable hi = 5
            let mutable idx = 0uy
            let mutable i = 0
                
            while i < bytes.Length do
                // do we need to use the next byte?
                if hi > 8 then
                    // get the last piece from the current byte, shift it to the right
                    // and increment the byte counter
                    idx <- bytes.[i] >>> (hi - 5)
                    i <- i + 1
                    if i <> bytes.Length then
                        // if we are not at the end, get the first piece from
                        // the next byte, clear it and shift it to the left
                        idx <- ((bytes.[i] <<< (16 - hi)) >>> 3) ||| idx

                    hi <- hi - 3
                elif hi = 8 then
                    idx <- bytes.[i] >>> 3
                    i <- i + 1
                    hi <- hi - 3
                else
                    // simply get the stuff from the current byte
                    idx <- (bytes.[i] <<< (8 - hi)) >>> 3
                    hi <- hi + 5

                b.Append (encodingIndex.[int idx]) |> ignore

            b.ToString ()