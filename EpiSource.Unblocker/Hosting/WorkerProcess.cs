using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// ReSharper disable IdentifierTypo

namespace EpiSource.Unblocker.Hosting {
    public sealed class WorkerProcess : IDisposable {
        // ReSharper disable once MemberCanBePrivate.Global
        public static readonly TimeSpan StartupTimeout = new TimeSpan(0, 0, 10);
        
        private readonly object processLock = new object();
        private bool disposed;
        private Process process;
        
        // ReSharper disable once MemberCanBePrivate.Global
        public static EventWaitHandle CreateWaitForProcessReadyHandle(Guid ipcguid) {
            return CreateWaitForProcessReadyHandle(ipcguid.ToString());
        }

        public static EventWaitHandle CreateWaitForProcessReadyHandle(string ipcguid) {
            return new EventWaitHandle(false, EventResetMode.ManualReset, 
                typeof(WorkerServerHost).FullName + ":" + ipcguid);
        }

        public event EventHandler ProcessDeadEvent;

        public bool IsAlive {
            get {
                var p = this.process;

                try {
                    return p != null && !p.HasExited;
                } catch (InvalidOperationException) { } catch (Win32Exception) { }

                return false;
            }
        }

        public int Id {
            get {
                var p = this.process;
                if (p == null) {
                    throw new InvalidOperationException("Id has not been set / process not active.");
                }

                return p.Id;
            }
        }

        public Process Process {
            get { return this.process; }
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
                        #if useInstallUtil
                        FileName = GetInstallUtilLocation(),
                        #else
                        FileName = bootstrapAssemblyPath.Value,
                        #endif
                        
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WorkingDirectory = typeof(WorkerServerHost).Assembly.Location + @"\..",
                        Arguments = string.Format(CultureInfo.InvariantCulture,
                            "/LogFile= /notransaction /ipcguid={0} /parentpid={1} /debug={2} {3}",
                            ipcguid, Process.GetCurrentProcess().Id, debug, typeof(WorkerServerHost).Assembly.Location),
                        RedirectStandardOutput = redirectConsole,
                        RedirectStandardError = redirectConsole
                    },
                    EnableRaisingEvents = true
                };

                this.process.Exited += (sender, args) => {
                    var handler = this.ProcessDeadEvent;
                    if (handler != null) {
                        handler(this, args);
                    }
                };
                
                if (redirectConsole) {
                    this.process.OutputDataReceived += (s, e) => Console.WriteLine(e.Data);
                    this.process.ErrorDataReceived += (s, e) => Console.WriteLine(e.Data);
                }

                // Start process and wait for it to be ready
                var waitForProcessReadyHandle = CreateWaitForProcessReadyHandle(ipcguid);

                try {
                    this.process.Start();
                } catch (Win32Exception e) {
                    // https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/18d8fbe8-a967-4f1c-ae50-99ca8e491d2d
                    const int ERROR_FILE_NOT_FOUND = 0x02;
                    const int ERROR_VIRUS_INFECTED = 0xE1;
                    const int ERROR_VIRUS_DELETED = 0xE2;

                    if (e.NativeErrorCode == ERROR_FILE_NOT_FOUND) {
                        throw new FileNotFoundException("Unblocker worker executable not found: " + this.process.StartInfo.FileName, this.process.StartInfo.FileName);
                    }
                    if (e.NativeErrorCode == ERROR_VIRUS_INFECTED || e.NativeErrorCode == ERROR_VIRUS_DELETED) {
                        throw new DeniedByVirusScannerFalsePositive(e, this.process.StartInfo.FileName);
                    } 
                
                    throw;
                }

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

                var server = WorkerServerClientSideProxy.ConnectToWorkerServer(ipcguid);
                return new WorkerClient(this, server);
            }
        }
        
        public void Dispose() {
            lock (this.processLock) {
                this.Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        // ReSharper disable once UnusedParameter.Local
        private /*protected virtual*/ void Dispose(bool disposing) {
            if (!this.disposed) {
                this.disposed = true;

                if (this.process != null) {
                    try {
                        // Dispose locks; finalizer should not
                        // ReSharper disable once InconsistentlySynchronizedField
                        this.process.Kill();
                    } catch (InvalidOperationException) {
                        // has already exited
                    }

                    // ReSharper disable once InconsistentlySynchronizedField
                    this.process.Dispose();
                    // ReSharper disable once InconsistentlySynchronizedField
                    this.process = null;
                }
            }
        }

        ~WorkerProcess() {
            this.Dispose(false);
        }
        
        private static string GetInstallUtilLocation() {
            return Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "InstallUtil.exe");
        }

        #if !useInstallUtil
        private static Lazy<string> bootstrapAssemblyPath = new Lazy<string>(WorkerServerHost.CreateBootstrapAssembly);
        #endif
        
    }

}