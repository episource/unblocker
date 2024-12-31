using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Text;
using System.Threading;

using Microsoft.CSharp;

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
        
        internal static string CreateBootstrapAssembly() {
            var knownAssembliesBuilder = new StringBuilder();
            var knownAssembliesList = AppDomain.CurrentDomain.GetAssemblies()
                                               .Where(a => !a.IsDynamic && File.Exists(a.Location))
                                               .GroupBy(a => a.FullName)
                                               .Select(g => g.First())
                                               .ToList();
            foreach (var a in knownAssembliesList) {
                knownAssembliesBuilder.AppendLine(String.Format(CultureInfo.InvariantCulture,
                    "knownAssemblies[\"{0}\"]=@\"{1}\";", a.FullName, a.Location));
            }

            var hostAssemblyLocation = typeof(WorkerServerHost).Assembly.Location;
            var hostClassName = typeof(WorkerServerHost).FullName;

            Expression<Action<string[]>> startMethod = args => WorkerServerHost.Start(args);
            var hostStartName = (startMethod.Body as MethodCallExpression).Method.Name;

            var source = @"
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;

namespace episource.unblocker.hosting {
    public static class Bootstrapper {
        
        public static void Main(string[] args) {
            IDictionary<string, string> knownAssemblies = new Dictionary<string, string>();
            " + knownAssembliesBuilder + @"

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
                if (knownAssemblies.ContainsKey(e.Name)) {
                    return Assembly.LoadFile(knownAssemblies[e.Name]);
                }

                return null;
            };

            Assembly hostAssembly = Assembly.LoadFile(@""" + hostAssemblyLocation + @""");
            Type hostType = hostAssembly.GetType(""" + hostClassName + @""");
            MethodInfo startMethod = hostType.GetMethod(""" + hostStartName + @""", new[] {typeof(IEnumerable<string>)});
            startMethod.Invoke(null, new [] { args });
        }
        
    }
}
             ";
            
            var provider = new CSharpCodeProvider();
            var opts = new CompilerParameters {
                GenerateInMemory = false,
                GenerateExecutable = true,
                MainClass = "episource.unblocker.hosting.Bootstrapper",
                ReferencedAssemblies = { "System.dll" }
            };

            var result = provider.CompileAssemblyFromSource(opts, source);
            if (result.NativeCompilerReturnValue != 0) {
                var ex = new InvalidOperationException("Failed to generate bootstrap assembly.");
                ex.Data["Errors"] = result.Errors;
                ex.Data["Output"] = result.Output;
                throw ex;
            }

            return result.PathToAssembly;
        }
    }
}