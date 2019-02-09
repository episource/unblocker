using System;
using System.Collections;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Threading;

namespace episource.unblocker.hosting {
    /// <summary>This is the entry point of the host process.
    /// To execute a class library the InstallUtil.exe application included with every .net framework installation
    /// is abused.</summary>
    [RunInstaller(true)]
    public sealed class WorkerServerHost : Installer {
        public override void Install(IDictionary stateSaver) {
            // serialization framework tries to find assembly on disk
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) 
                => args.Name == typeof(WorkerServerHost).Assembly.FullName ? typeof(WorkerServerHost).Assembly : null;
            
            if (this.Context.Parameters["debug"] == DebugMode.Debugger.ToString()) {
                while (!Debugger.IsAttached) {
                    Debugger.Launch();
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "[server:{0}] Waiting for debugger", Process.GetCurrentProcess().Id));
                    Thread.Sleep(1000);
                }
            }
            
            var ipcGuidString = this.Context.Parameters["ipcguid"];
            var parentPid = Convert.ToInt32(this.Context.Parameters["parentpid"]);
            
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