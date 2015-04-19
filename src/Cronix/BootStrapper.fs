﻿namespace Cronix

open System.ServiceProcess
open System.IO
open Logging
open System
open Owin
open Microsoft.AspNet.SignalR.Hubs
open Microsoft.AspNet.SignalR
open Microsoft.Owin.Hosting
open Microsoft.Owin.Cors

module WebServices = 
    let logger = logger()
    let hostScheduleManager(scheduleManager : IScheduleManager) =
        try
            let options = new StartOptions()
            options.Port <- new Nullable<int>(8111)
            let config = new HubConfiguration(EnableDetailedErrors = true)
       
            WebApp.Start(options,
               fun(app : Owin.IAppBuilder) -> (
                                               GlobalHost.DependencyResolver.Register(typeof<IScheduleManager>, fun() -> scheduleManager :> obj)
                                               app.UseCors(CorsOptions.AllowAll) |> ignore
                                               Owin.OwinExtensions.MapSignalR(app, "/signalr", config) |> ignore
               )) |> ignore
        with
        | exn ->  logger.Error(exn)

type IScheduleManagerHub =
   abstract member GetData: JobState[] -> unit
   abstract member OnStateChanged : JobState -> unit
   abstract member StartJob: string -> unit
   abstract member FireJob: string -> unit
   abstract member StopJob: string -> unit


[<HubName("ScheduleManager")>]
type ScheduleManagerHub() as self =
    inherit Hub<IScheduleManagerHub>()

    // TODO: Service locator antipattern, but it works just for now...
    let scheduleManager = GlobalHost.DependencyResolver.Resolve(typeof<IScheduleManager>) :?> IScheduleManager
    let state() = scheduleManager.State |> Seq.toArray

    do
        scheduleManager.OnStateChanged.AddHandler(fun sender args -> self.onStateChangedHandler args)

    member private self.onStateChangedHandler args =
        base.Clients.All.OnStateChanged(args)

    member self.GetData() =
        base.Clients.All.GetData(state()) |> ignore

    member self.StartJob(name : string) =
        scheduleManager.StartJob(name) |> ignore

    member self.FireJob(name : string) =
        base.Clients.All.GetData(state()) |> ignore

    member self.StopJob(name : string) =
        scheduleManager.StopJob(name) |> ignore

/// An adapter responsible for starting, stopping and shutting down the cronix service.
type ServiceProcessAdapter(service : IScheduleManager, setup) =
    inherit ServiceBase()
    let logger = logger()
    do
        (* Set default directory for windows service *)
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory)

    ///Starts the ScheduleManager and performs the manager setup
    override x.OnStart(args : string[]) = 
        logger.Debug("starting service")
        service.Start()
        WebServices.hostScheduleManager(service) |> ignore
        setup()
         
    /// Manually starts the schedulemanager
    member x.Start() =
        x.OnStart(null)
        
    /// Stops the ScheduleManager
    override x.OnStop() = 
        logger.Debug("stopping service")
        service.Stop()

    /// Stops the ScheduleManager
    override x.OnShutdown() =
        logger.Debug("shutting down service")
        service.Stop()

/// Module responsible for cronix service initialization.
module BootStrapper =
   
    open Chessie.ErrorHandling
    open Logging
    open Compiler
    open System.IO
    open System
    open System.ServiceProcess
    open System.Reflection

    /// Boostrapper module specific logger.
    let logger = logger()

    /// Invokes the compiled startup script.
    let invokeStartupScript(state : StartupScriptState) =
        try
            match state.compiled with
            | None -> fail(sprintf "Compile code has not been craeted.")
            | Some assembly ->  let startMethod  = assembly.GetType("Cronix.Startup.RunAtStartup").GetMethod("start")
                                let startInvoke (scheduler : IScheduleManager) = startMethod.Invoke(null, [|box (scheduler:IScheduleManager);|]) :?> unit
                                startInvoke(state.scheduleManager)
                                ok state
        with
            | exn -> fail(sprintf "Failed to run startup script. %s" exn.Message)
    
    /// Invokes the startup handler.
    let invokeStartupHandler (state: StartupHandlerState) =
        match state.startupHandler with
        | None -> ok state
        | Some handler -> 
            try
                handler.Invoke(state.scheduleManager)
                ok state
            with
            | exn -> fail(sprintf "Failed to run a startup handler. %s" exn.Message)

    /// Setups up the cronix service
    let setupService(scheduleManager : IScheduleManager) (startupHandler : Option<StartupHandler>) =
        let startupScriptState scheduleManager referencedAssemblies  source = 
            { StartupScriptState.scheduleManager = scheduleManager; 
              referencedAssemblies = referencedAssemblies;
              source = source; 
              compiled = None;
            }
        let startupHandlerState scheduleManager startupHandler = 
            { StartupHandlerState.scheduleManager = scheduleManager; startupHandler = startupHandler;}

        startupHandlerState
        <!> ok scheduleManager
        <*> ok startupHandler
        >>= invokeStartupHandler
        |> logResult
        |> ignore

        if File.Exists(startupFile) = true then
            startupScriptState
            <!> ok scheduleManager
            <*> loadAssemblies()
            <*> getStartupScript()
            >>= compileScript
            >>= invokeStartupScript
            |> logResult 
            |> ignore

    /// Runs the cronix service. If its run in console mode then the service will start immediately. If not then the ServiceProcessAdapter will start.
    let runService(startupHandler : Option<StartupHandler>) debug =
        try
            AppDomain.CurrentDomain.UnhandledException.AddHandler(fun(_) (args:UnhandledExceptionEventArgs) -> logger.Fatal(args.ExceptionObject))
            let scheduleManager = new ScheduleManager() :> IScheduleManager
            let setup() = 
                setupService scheduleManager startupHandler |> ignore 
            use processAdapter = new ServiceProcessAdapter(scheduleManager, setup)
            if debug = true then
                processAdapter.Start() |> ignore
                Console.Read() |> ignore
                processAdapter.Stop()
            else
                ServiceBase.Run(processAdapter)
                
        with
        | exn ->  logger.Fatal(exn)

    /// Prints the user guide.
    let printGuide() =
        let entryAsm = Assembly.GetEntryAssembly()
        let executingAsm =  Assembly.GetExecutingAssembly()
        let assemblyName = if entryAsm <> null then entryAsm.GetName().Name else executingAsm.GetName().Name
        printfn "Ussage:"
        printfn " %s debug" assemblyName
        printfn "    Starts the service in interactive mode."
        printfn "  %s install" assemblyName
        printfn "     Installs %s as a Windows service." assemblyName
        printfn "  %s uninstall" assemblyName
        printfn "     Uninstalls %s as a Windows service." assemblyName
    
    /// Returns true if service is run from console.
    let isDebug() = Environment.UserInteractive
 
    /// Changes the Some(null) into None value.
    let parseOption (input : Option<'a>) =
        match input with
        | None -> None
        | Some(null) -> None
        | Some(a') -> Some(a')

    /// Initializes the cronix service
    let InitService : InitService =
        fun (args, startupHandler) ->
           
            let args' = parseOption(Some(args))
            let startupHandler' = parseOption(Some(startupHandler))
            
            if args'.IsNone || (args'.IsSome && args'.Value.Length = 0) then 
                runService startupHandler' <| isDebug()
                ok("runService")

            else if args'.IsSome && args'.Value.Length = 1 then
                match args'.Value.[0] with
                | "install" -> ProjectInstaller.Install(Assembly.GetEntryAssembly())
                | "uninstall" -> ProjectInstaller.Uninstall(Assembly.GetEntryAssembly())
                | "debug" -> runService startupHandler' <| isDebug()
                             ok("runService")
                | _ -> printGuide()
                       ok("printGuide") 
            else
               printGuide()
               ok("printGuide")




//TODO:

// Start - if strigger is stopped
// Fire - run just now
// Stop - stop trigger it will not run
// Add next occurance date
// Jobs Preview --> Triggers !!????