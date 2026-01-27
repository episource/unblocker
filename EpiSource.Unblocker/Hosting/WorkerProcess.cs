using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using EpiSource.Unblocker.Util;

// ReSharper disable IdentifierTypo

namespace EpiSource.Unblocker.Hosting {
    public sealed class WorkerProcess : IDisposable {
        // ReSharper disable once MemberCanBePrivate.Global
        public static readonly TimeSpan StartupTimeout = new TimeSpan(0, 0, 5);
        
        private readonly object processLock = new object();
        private readonly Process process;
        private readonly Guid ipcguid;
        private bool disposed;
        
        
        private WorkerProcess(Process process, Guid ipcguid) {
            this.process = process;
            this.ipcguid = ipcguid;
            
            process.Exited += (sender, args) => {
                var handler = this.ProcessDeadEvent;
                if (handler != null) {
                    handler(this, args);
                }
            };
            
            AppDomain.CurrentDomain.ProcessExit += this.OnParentProcessExit;
        }
        
        public static async Task<WorkerProcess> StartAsync(
                CancellationToken ct = default(CancellationToken), BootstrapAssemblyProvider bootstrapAssemblyProvider = null,
                DebugMode debug = DebugMode.None
            ) {
            var ipcguid = Guid.NewGuid();
            var redirectConsole = debug != DebugMode.None;
            var startupConsoleOutputBuffer = new StringBuilder();
            DataReceivedEventHandler startupConsoleOutputHandler = (s, e) => startupConsoleOutputBuffer.AppendLine(e.Data);
            
            var process = new Process {
                StartInfo = {
                    FileName = bootstrapAssemblyProvider != null ? await bootstrapAssemblyProvider.EnsureAvailableAsync() :  GetInstallUtilLocation(),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Application.ExecutablePath + @"\..",
                    Arguments = string.Format(CultureInfo.InvariantCulture,
                        "/LogFile= /LogToConsole=true /InstallType=NoTransaction /ipcguid={0} /parentpid={1} /debug={2} {3}",
                        ipcguid, Process.GetCurrentProcess().Id, debug, typeof(WorkerServerHost).Assembly.Location),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += startupConsoleOutputHandler;
            process.ErrorDataReceived += startupConsoleOutputHandler;

            if (redirectConsole) {
                process.OutputDataReceived += (s, e) => Console.WriteLine(e.Data);
                process.ErrorDataReceived += (s, e) => Console.WriteLine(e.Data);
            }

            try {
                process.Start();
            } catch (Win32Exception e) {
                // https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/18d8fbe8-a967-4f1c-ae50-99ca8e491d2d
                const int ERROR_FILE_NOT_FOUND = 0x02;
                const int ERROR_VIRUS_INFECTED = 0xE1;
                const int ERROR_VIRUS_DELETED = 0xE2;

                if (e.NativeErrorCode == ERROR_FILE_NOT_FOUND) {
                    throw new FileNotFoundException("Unblocker worker executable not found: " + process.StartInfo.FileName, process.StartInfo.FileName);
                }
                if (e.NativeErrorCode == ERROR_VIRUS_INFECTED || e.NativeErrorCode == ERROR_VIRUS_DELETED) {
                    throw new DeniedByVirusScannerFalsePositive(e, process.StartInfo.FileName);
                }

                process.Dispose();
                throw;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutMs = debug == DebugMode.Debugger ? -1 : (int)StartupTimeout.TotalMilliseconds;
            var waitForProcessReadyHandle = CreateWaitForProcessReadyHandle(ipcguid);
            
            var isReady = false;
            var isCancelled = false;
            try {
                isReady = await waitForProcessReadyHandle.WaitOneAsync(timeoutMs, ct);
            } catch (TaskCanceledException) {
                isCancelled = true;
            }

            if (!isReady) {
                try {
                    process.Kill();
                    process.Dispose();
                } catch (Exception) {
                    // already did my best - nothing more left to do
                }

                if (isCancelled) {
                    throw new TaskCanceledException();
                }
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "Failed to start unblocker process. Wasn't ready within {0}s!{1}",
                    StartupTimeout.TotalSeconds,
                    startupConsoleOutputBuffer.Length > 0 
                        ? "\n\nProcess Console Output:\n" + startupConsoleOutputBuffer + "\n"
                        : ""));
            }
            
            process.OutputDataReceived -= startupConsoleOutputHandler;
            process.ErrorDataReceived -= startupConsoleOutputHandler;
            if (!redirectConsole) {
                process.CancelOutputRead();
                process.CancelErrorRead();
            }

            return new WorkerProcess(process, ipcguid);
        }
        
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

        public Guid Ipcguid {
            get {
                return this.ipcguid;
            }
        }

        public Process Process {
            get { return this.process; }
        }
        
        private void OnParentProcessExit(object sender, EventArgs e) {
            this.Dispose();
        }
        
        #region IDisposable
        
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

                AppDomain.CurrentDomain.ProcessExit -= this.OnParentProcessExit;

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
                }
            }
        }

        ~WorkerProcess() {
            this.Dispose(false);
        }
        
        #endregion
        
        private static string GetInstallUtilLocation() {
            return Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "InstallUtil.exe");
        }

    }

}