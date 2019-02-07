using System;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Threading;
// ReSharper disable IdentifierTypo

namespace episource.unblocker.hosting {
    public sealed class WorkerProcess : IDisposable {
        // ReSharper disable once MemberCanBePrivate.Global
        public static readonly TimeSpan StartupTimeout = new TimeSpan(0, 0, 10);

        private readonly object processLock = new object();
        private bool disposed = false;
        private Process process;
        
        // ReSharper disable once MemberCanBePrivate.Global
        public static EventWaitHandle CreateWaitForProcessReadyHandle(Guid ipcguid) {
            return CreateWaitForProcessReadyHandle(ipcguid.ToString());
        }

        public static EventWaitHandle CreateWaitForProcessReadyHandle(string ipcguid) {
            return new EventWaitHandle(false, EventResetMode.ManualReset, 
                typeof(WorkerServerHost).FullName + ":" + ipcguid);
        }

        public bool IsAlive {
            get {
                lock (this.processLock) {
                    return this.process != null && !this.process.HasExited;
                }
            }
        }

        public int Id {
            get {
                lock (this.processLock) {
                    return this.process.Id;
                }
            }
        }

        [SuppressMessage("ReSharper", "StringLiteralTypo")]
        public WorkerClient Start(DebugMode debug = DebugMode.None) {
            lock (this.processLock) {
                if (this.disposed) {
                    throw new ObjectDisposedException("WorkerProcess has been disposed.");
                }
                if (this.process != null) {
                    throw new InvalidOperationException("Process already started.");
                }

                var ipcguid = Guid.NewGuid();

                var redirectConsole = debug != DebugMode.None;
                this.process = new Process {
                    StartInfo = {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WorkingDirectory = typeof(WorkerServerHost).Assembly.Location + @"\..",
                        Arguments = string.Format(CultureInfo.InvariantCulture,
                            "/LogFile= /notransaction /ipcguid={0} /parentpid={1} /debug={2} {3}",
                            ipcguid, Process.GetCurrentProcess().Id, debug, typeof(WorkerServerHost).Assembly.Location),
                        FileName = GetInstallUtilLocation(),
                        RedirectStandardOutput = redirectConsole,
                        RedirectStandardError = redirectConsole
                    }
                };

                if (redirectConsole) {
                    this.process.OutputDataReceived += (s, e) => Console.WriteLine(e.Data);
                    this.process.ErrorDataReceived += (s, e) => Console.WriteLine(e.Data);
                }

                // Start process and wait for it to be ready
                var waitForProcessReadyHandle = CreateWaitForProcessReadyHandle(ipcguid);
                this.process.Start();

                if (redirectConsole) {
                    this.process.BeginOutputReadLine();
                    this.process.BeginErrorReadLine();
                }

                var timeoutMs = debug == DebugMode.Debugger ? -1 : (int)StartupTimeout.TotalMilliseconds;
                var isReady = waitForProcessReadyHandle.WaitOne(timeoutMs, false);
                
                if (!isReady) {
                    try {
                        this.process.Kill();
                    } catch (Exception) {
                        // already did my best - nothing more left to do
                    }

                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "Failed to start unblocker process. Wasn't ready within {0}s!",
                        StartupTimeout.TotalSeconds));
                }

                IWorkerServer server = WorkerServerClientSideProxy.ConnectToWorkerServer(ipcguid);
                return new WorkerClient(this, server);
            }
        }
        
        public void Dispose() {
            lock (this.processLock) {
                this.Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        protected /*virtual*/ void Dispose(bool disposing) {
            if (!this.disposed) {
                this.disposed = true;

                try {
                    this.process.Kill();
                } catch (InvalidOperationException e) {
                    // has already exited
                }

                this.process.Dispose();
                this.process = null;
            }
        }

        ~WorkerProcess() {
            this.Dispose(false);
        }
        
        private static string GetInstallUtilLocation() {
            return Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "InstallUtil.exe");
        }
    }
}