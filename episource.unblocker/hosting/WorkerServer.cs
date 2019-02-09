using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Remoting.Lifetime;
using System.Security;
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
        private readonly string serverId = "[server:" + Process.GetCurrentProcess().Id + "]";
        private readonly ClientSponsor proxyLifetimeSponsor = new ClientSponsor();
        private volatile bool isReady = true;
        private volatile TaskRunner activeRunner;
        private volatile AppDomain activeRunnerDomain;
        private volatile Task cleanupTask;

        public event EventHandler TaskCanceledEvent;

        public event EventHandler<TaskSucceededEventArgs> TaskSucceededEvent;
        public event EventHandler<TaskFailedEventArgs> TaskFailedEvent;

        public event EventHandler ServerDyingEvent;

        public event EventHandler ServerReadyEvent;
        
        
        
        public void Cancel(TimeSpan cancelTimeout) {
            lock (this.stateLock) {
                if (this.activeRunner != null && this.cleanupTask == null) {
                    this.activeRunner.Cancel();
                    this.cleanupTask = this.EnsureCanceled(cancelTimeout);
                }
            }
        }
        
        // returns when invocation is started, but before it returns
        // end of invocation is signaled via TaskCompletionSourceProxy
        public void InvokeAsync(
            InvocationRequest.PortableInvocationRequest invocationRequest, SecurityZone securityZone
        ) {
            if (invocationRequest == null) {
                throw new ArgumentNullException("invocationRequest");
            }
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
                
                // ReSharper disable once AssignNullToNotNullAttribute
                this.activeRunnerDomain = AppDomain.CreateDomain(taskDomainName, AppDomain.CurrentDomain.Evidence,
                    new AppDomainSetup {
                        ApplicationBase = invocationRequest.ApplicationBase,
                        LoaderOptimization = LoaderOptimization.MultiDomainHost
                    }, zonePermissions, typeof(WorkerServer).GetStrongNameOfAssemblyAsArray());

                this.activeRunner = (TaskRunner) this.activeRunnerDomain.CreateInstanceFromAndUnwrap(
                    typeof(TaskRunner).Assembly.Location, typeof(TaskRunner).FullName);
                this.proxyLifetimeSponsor.Register(this.activeRunner);
            }
            
            // Note: The runner's Cancel(..) method might have been invoked before the task is actually started.
            // However, this is OK. The runner is a oneshot thing. If it is canceled before it receives a invocation
            // request, it goes to canceled state and won't even start the task.
            Task.Run(() => this.activeRunner.InvokeSynchronously(this, invocationRequest));
        }

        private void OnRunnerSucceeded(TaskRunner runner, object result) {
            lock (this.stateLock) {
                if (this.IsActiveRunner(runner)) {
                    var taskSucceededEvent = this.TaskSucceededEvent;
                    if (taskSucceededEvent != null) {
                        taskSucceededEvent(this, new TaskSucceededEventArgs(result));
                    }

                    this.Cleanup(true);
                }
            }
        }

        private void OnRunnerFailed(TaskRunner runner, Exception e) {
            lock (this.stateLock) {
                if (this.IsActiveRunner(runner)) {
                    var taskFailedEvent = this.TaskFailedEvent;
                    if (taskFailedEvent != null) {
                        taskFailedEvent(this, new TaskFailedEventArgs(e));
                    }

                    this.Cleanup(true);
                }
            }
        }

        private void OnRunnerCanceled(TaskRunner runner) {
            lock (this.stateLock) {
                if (this.IsActiveRunner(runner)) {
                    var taskCanceledEvent = this.TaskCanceledEvent;
                    if (taskCanceledEvent != null) {
                        taskCanceledEvent(this, EventArgs.Empty);
                    }

                    this.Cleanup(true);
                }
            }
        }

        private bool IsActiveRunner(TaskRunner runner) {
            lock (this.stateLock) {
                if (runner != this.activeRunner) {
                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture, "{0} runner ({1}) != this.activeRunner ({2})",
                        this.serverId, runner, this.activeRunner));
                    return false;
                }

                return true;
            }
        }

        private async Task EnsureCanceled(TimeSpan cancelTimeout) {
            await Task.Delay(cancelTimeout).ConfigureAwait(false);
            this.Cleanup(false);
        }

        // unload appdomain
        private void Cleanup(bool cleanShutdown) {
            // lock might be kept for a very long time
            // this is ok, as in this case, the worker isn't ready anyhow
            lock (this.stateLock) {
                // cleanup task has executed!
                this.cleanupTask = null;
                
                if (this.isReady) {
                    // nothing to cleanup - already clean
                    return;
                }

                if (!cleanShutdown) {
                    Console.WriteLine(
                        this.serverId + " Failed to cancel task. Going to kill the task. Let's tell.");
                    this.TaskCanceledEvent(this, EventArgs.Empty);
                }
                

                try {
                    Console.WriteLine(this.serverId + " Going to unload the task's AppDomain.");
                    
                    this.activeRunner.NotifyUnload();
                    this.proxyLifetimeSponsor.Unregister(this.activeRunner);
                    this.activeRunner = null;
                    
                    AppDomain.Unload(this.activeRunnerDomain);
                    this.activeRunnerDomain = null;
                    
                    Console.WriteLine(this.serverId + " Done unloading the task's AppDomain.");

                    this.isReady = true;
                    this.ServerReadyEvent(this, EventArgs.Empty);
                } catch (CannotUnloadAppDomainException e) {
                    Console.WriteLine(this.serverId + " Failed to unload task's AppDomain: " + e.Message);
                    Console.WriteLine(this.serverId + " Going to kill myself!");

                    this.ServerDyingEvent(this, EventArgs.Empty); 
                    
                    // kill current worker in the most robust way possible!
                    try {
                        Process.GetCurrentProcess().Kill();
                    } catch (Exception ee) {
                        Console.WriteLine(this.serverId + " Failed to commit suicide: " + ee.Message);
                        Console.WriteLine(ee.StackTrace);
                        Console.WriteLine(this.serverId + " Client will have to take care of that!");   
                    }
                }
            }
        }

        public override string ToString() {
            return this.serverId;
        }

        public void Dispose() {
            lock (this.stateLock) {
                this.Dispose(true);
            }
        }

        private /*protected virtual*/ void Dispose(bool disposing) {
            if (disposing) {
                this.Cancel(TimeSpan.FromMilliseconds(50));
            }
        }
    }
}