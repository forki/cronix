﻿#if INTERACTIVE
#I "."
#I "bin\Debug"
#I "bin\Release"
#r "Cronix.dll"
#r "Chessie.dll"
#endif

namespace Cronix.Startup

module RunAtStartup =
    
    open System.Threading
    open System
    open Cronix
    open Messages

    let sampleJob (token : CancellationToken) = 
      printf "callback executed at (UTC) %s\n" <| DateTime.UtcNow.ToString()
      Thread.Sleep 100

    let start (scheduler : IScheduleManager) =
        scheduler.ScheduleJob "job1" "* * * * *" <| JobCallback(sampleJob) |> ignore