using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Lifetime;
using System.Security;
using System.Security.Policy;
using System.Threading;
using System.Threading.Tasks;

namespace episource.unblocker.hosting {
    public interface IWorkerServer : IDisposable {
        event EventHandler<TaskCanceledEventArgs> TaskCanceledEvent;

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

        public event EventHandler<TaskCanceledEventArgs> TaskCanceledEvent;

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
            
            Task.Run(() => {
                // the only way for the runner to throw is an forced shutdown of the appdomain!
                var result = this.activeRunner.InvokeSynchronously(this, invocationRequest);

                try {
                    this.OnRunnerDone(result);
                } finally {
                    this.Cleanup(true);
                }
            });
            
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture, "{0} Started executing invocation request.",
                this.serverId));
        }

        private void OnRunnerDone(EventArgs result) {
            if (result is TaskSucceededEventArgs) {
                this.OnRunnerDone(this.TaskSucceededEvent,
                    e => e(this, (TaskSucceededEventArgs)result),
                    "SUCCESS");
            } else if (result is TaskCanceledEventArgs) {
                this.OnRunnerDone(this.TaskCanceledEvent,
                    e => e(this, (TaskCanceledEventArgs) result),
                    "CANCELED");
            } else if (result is TaskFailedEventArgs) {
                var failedArgs = result as TaskFailedEventArgs;
                var ex = failedArgs.Exception;
                
                var msg = "EXCEPTION";
                if (ex != null) {
                    msg += " " + ex.GetType() + " - " + ex.Message;
                }
                
                this.OnRunnerDone(this.TaskFailedEvent,
                    e => e(this, failedArgs), msg);
            } else {
                throw new ArgumentException("Unknown result type.", "result");
            }
        }

        private void OnRunnerDone<T>(T eventHandler, Action<T> handlerInvocation, string resultMsg) {
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture, "{0} Done executing invocation request. Result: {1}",
                this.serverId, resultMsg));
            
            eventHandler.InvokeEvent(handlerInvocation);                  
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
                    this.TaskCanceledEvent.InvokeEvent(
                        e => e(this, new TaskCanceledEventArgs(false)));
                }
                

                try {
                    Console.WriteLine(this.serverId + " Going to unload the task's AppDomain.");
                    
                    this.proxyLifetimeSponsor.Unregister(this.activeRunner);
                    this.activeRunner = null;

                    AppDomain.Unload(this.activeRunnerDomain);
                    this.activeRunnerDomain = null;
                    
                    Console.WriteLine(this.serverId + " Done unloading the task's AppDomain.");

                    this.isReady = true;
                    this.ServerReadyEvent.InvokeEvent(e => e(this, EventArgs.Empty));
                } catch (CannotUnloadAppDomainException ex) {
                    Console.WriteLine(this.serverId + " Failed to unload task's AppDomain: " + ex.Message);
                    Console.WriteLine(this.serverId + " Going to kill myself!");

                    this.ServerDyingEvent.InvokeEvent(e => e(this, EventArgs.Empty));
                    
                    // kill current worker in the most robust way possible!
                    try {
                        Process.GetCurrentProcess().Kill();
                    } catch (Exception exx) {
                        Console.WriteLine(this.serverId + " Failed to commit suicide: " + exx.Message);
                        Console.WriteLine(exx.StackTrace);
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