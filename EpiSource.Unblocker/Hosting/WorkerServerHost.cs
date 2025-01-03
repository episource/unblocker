using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Threading;

namespace EpiSource.Unblocker.Hosting {
    /// <summary>This is the entry point of the host process.
    /// To execute a class library the InstallUtil.exe application included with every .net framework installation
    /// is abused.</summary>
    #if useInstallUtil
    [RunInstaller(true)]
    public sealed class WorkerServerHost : System.Configuration.Install.Installer {
    
        public override void Install(IDictionary stateSaver) {
            Start(this.Context.Parameters);
        }
    #else
    public sealed class WorkerServerHost {
    #endif
        public static void Start(IEnumerable<string> args) {
            var argsDict = new StringDictionary();
            
            foreach (var arg in args) {
                var parts = arg.Split('=');
                argsDict[parts[0].Substring(1)] = parts.Length == 2 ? parts[1] : "";
            }
            
            Start(argsDict);
        }
        
        private static void Start(StringDictionary args) {
            if (!args.ContainsKey("debug")) {
                throw new ArgumentException("Missing argument `debug`.");
            }
            if (!args.ContainsKey("ipcguid")) {
                throw new ArgumentException("Missing argument `ipcguid`.");
            }
            if (!args.ContainsKey("parentpid")) {
                throw new ArgumentException("Missing argument `parentpid`.");
            }
            
            DebugMode debugMode;
            if (!DebugMode.TryParse(args["debug"], out debugMode)) {
                throw new ArgumentException("Invalid value of `debug`: " + args["debug"]);
            }

            int parentPid;
            if (!int.TryParse(args["parentpid"], out parentPid)) {
                throw new ArgumentException("Invalid value of `parentpid`: " + args["parentpid"]);
            }
            
            Start(debugMode, args["ipcguid"], parentPid);
        }

        private static void Start(DebugMode debugMode, string ipcGuidString, int parentPid) {
            // serialization framework tries to find assembly on disk
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) 
                => args.Name == typeof(WorkerServerHost).Assembly.FullName ? typeof(WorkerServerHost).Assembly : null;
            
            if (debugMode == DebugMode.Debugger) {
                while (!Debugger.IsAttached) {
                    Debugger.Launch();
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "[server:{0}] Waiting for debugger", Process.GetCurrentProcess().Id));
                    Thread.Sleep(1000);
                }
            }
            
            IDictionary ipcProperties = new Hashtable();
            ipcProperties["name"] = "UnblockerServerChannel";
            ipcProperties["portName"] = ipcGuidString;
            ipcProperties["typeFilterLevel"] = "Full";
            var ipcChannel = new IpcChannel(ipcProperties,
                new BinaryClientFormatterSinkProvider(ipcProperties, null),
                new BinaryServerFormatterSinkProvider(ipcProperties, null));
            
            ChannelServices.RegisterChannel(ipcChannel, false);
                        
            // Create and expose server
            var server = new WorkerServer();
            RemotingServices.Marshal(server, server.GetType().FullName);
            
            // permit client to wait for this process to be ready
            var waitForProcessReadyHandle = WorkerProcess.CreateWaitForProcessReadyHandle(ipcGuidString);
            waitForProcessReadyHandle.Set();
            waitForProcessReadyHandle.Close();

            try {
                Process.GetProcessById(parentPid).WaitForExit();
            } catch {
                // exit server process anyway
            }
            
            Environment.Exit(0);
        }
    }
}