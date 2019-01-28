using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Remoting.Lifetime;
using System.Security;
using System.Security.Permissions;
using System.Security.Policy;
using System.Threading.Tasks;

namespace episource.unblocker.hosting {
    public interface IWorkerServer : IDisposable {
        event EventHandler TaskCanceledEvent;

        event EventHandler<TaskSucceededEventArgs> TaskSucceededEvent;
        event EventHandler<TaskFailedEventArgs> TaskFailedEvent;

        event EventHandler ServerDyingEvent;

        event EventHandler ServerReadyEvent;

        void Cancel(TimeSpan cancelTimeout);

        void InvokeAsync(InvocationRequest.PortableInvocationRequest invocationRequest, SecurityZone securityZone);
    }
    
    public sealed partial class WorkerServer : MarshalByRefObject, IWorkerServer {
        private readonly object stateLock = new object();
        private readonly ClientSponsor proxyLifetimeSponsor = new ClientSponsor();
        private volatile bool isReady = true;
        private TaskRunner activeRunner;
        private AppDomain activeRunnerDomain;

        public event EventHandler TaskCanceledEvent;

        public event EventHandler<TaskSucceededEventArgs> TaskSucceededEvent;
        public event EventHandler<TaskFailedEventArgs> TaskFailedEvent;

        public event EventHandler ServerDyingEvent;

        public event EventHandler ServerReadyEvent;
        
        
        
        public void Cancel(TimeSpan cancelTimeout) {
            lock (this.stateLock) {
                if (this.activeRunner != null) {
                    this.activeRunner.Cancel();
                }

                this.EnsureCanceled(cancelTimeout);
            }
        }
        
        // returns when invocation is started, but before it returns
        // end of invocation is signaled via TaskCompletionSourceProxy
        public void InvokeAsync(
            InvocationRequest.PortableInvocationRequest invocationRequest, SecurityZone securityZone
        ) {
            lock (this.stateLock) {
                if (!this.isReady) {
                    throw new InvalidOperationException(
                        "Not ready: currently executing another task or cleaning up.");
                }
                this.isReady = false;


                var zoneEvidence = new Evidence();
                zoneEvidence.AddHostEvidence(new Zone(securityZone));
                var zonePermissions = SecurityManager.GetStandardSandbox(zoneEvidence);

                var taskDomainName = string.Format(CultureInfo.InvariantCulture, "{0}_{1}",
                    invocationRequest.MethodName,  DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                this.activeRunnerDomain = AppDomain.CreateDomain(taskDomainName, AppDomain.CurrentDomain.Evidence,
                    new AppDomainSetup {
                        ApplicationBase = invocationRequest.ApplicationBase,
                        LoaderOptimization = LoaderOptimization.MultiDomainHost
                    }, zonePermissions, typeof(WorkerServer).GetStrongNameOfAssemblyAsArray());

                this.activeRunner = (TaskRunner) this.activeRunnerDomain.CreateInstanceFromAndUnwrap(
                    typeof(TaskRunner).Assembly.Location, typeof(TaskRunner).FullName);
                this.proxyLifetimeSponsor.Register(this.activeRunner);
            }

            Task.Run(() => this.activeRunner.InvokeSynchronously(this, invocationRequest));
        }

        private void OnRunnerSucceeded(TaskRunner runner, object result) {
            lock (this.stateLock) {
                if (this.IsActiveRunner(runner)) {
                    this.TaskSucceededEvent(this, new TaskSucceededEventArgs(result));
                    this.Cleanup(true);
                }
            }
        }

        private void OnRunnerFailed(TaskRunner runner, Exception e) {
            lock (this.stateLock) {
                if (this.IsActiveRunner(runner)) {
                    this.TaskFailedEvent(this, new TaskFailedEventArgs(e));
                    this.Cleanup(true);
                }
            }
        }

        private void OnRunnerCanceled(TaskRunner runner) {
            lock (this.stateLock) {
                if (this.IsActiveRunner(runner)) {
                    this.TaskCanceledEvent(this, EventArgs.Empty);
                    this.Cleanup(true);
                }
            }
        }

        private bool IsActiveRunner(TaskRunner runner) {
            lock (this.stateLock) {
                if (runner != this.activeRunner) {
                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "runner ({0}) != this.activeRunner ({1})", 
                        runner, this.activeRunner));
                    return false;
                }

                return true;
            }
        }

        private async void EnsureCanceled(TimeSpan cancelTimeout) {
            await Task.Delay(cancelTimeout).ConfigureAwait(false);
            this.Cleanup(false);
        }

        // unload appdomain
        private void Cleanup(bool cleanShutdown) {
            lock (this.stateLock) {
                if (this.isReady) {
                    // nothing to cleanup - already clean
                    return;
                }

                if (!cleanShutdown) {
                    Console.WriteLine("Failed to cancel task. Going to kill the task. Let's tell.");
                    this.TaskCanceledEvent(this, EventArgs.Empty);
                }
                

                try {
                    Console.WriteLine("Going to unload the task's AppDomain.");
                    
                    this.activeRunner.NotifyUnload();
                    this.proxyLifetimeSponsor.Unregister(this.activeRunner);
                    this.activeRunner = null;
                    
                    AppDomain.Unload(this.activeRunnerDomain);
                    this.activeRunnerDomain = null;
                    
                    Console.WriteLine("Done unloading the task's AppDomain.");

                    this.isReady = true;
                    this.ServerReadyEvent(this, EventArgs.Empty);
                } catch (CannotUnloadAppDomainException e) {
                    this.ServerDyingEvent(this, EventArgs.Empty);
                    
                    Console.WriteLine("Failed to unload task's AppDomain: " + e.Message);
                    Console.WriteLine(e.StackTrace);
                    Console.WriteLine("Going to kill myself!");    
                    
                    // kill current worker in the most robust way possible!
                    try {
                        Process.GetCurrentProcess().Kill();
                    } catch (Exception ee) {
                        Console.WriteLine("Failed to commit suicide: " + ee.Message);
                        Console.WriteLine(ee.StackTrace);
                        Console.WriteLine("Client will have to take care of that!");   
                    }
                }
            }
        }

        public void Dispose() {
            lock (this.stateLock) {
                this.Dispose(true);
            }
        }

        protected /*virtual*/ void Dispose(bool disposing) {
            if (disposing) {
                this.Cancel(TimeSpan.FromMilliseconds(50));
            }
        }
    }
}