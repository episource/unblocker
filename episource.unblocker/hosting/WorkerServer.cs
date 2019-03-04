using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Remoting.Lifetime;
using System.Runtime.Serialization;
using System.Security;
using System.Security.Policy;
using System.Threading;
using System.Threading.Tasks;

namespace episource.unblocker.hosting {
    public interface IWorkerServer : IDisposable {
        event EventHandler<TaskSucceededEventArgs> TaskSucceededEvent;
        event EventHandler<TaskCanceledEventArgs> TaskCanceledEvent;
        event EventHandler<TaskFailedEventArgs> TaskFailedEvent;
        event EventHandler ServerDyingEvent;
        event EventHandler ServerReadyEvent;

        void Cancel(TimeSpan cancelTimeout, ForcedCancellationMode forcedCancellationMode);

        void InvokeAsync(InvocationRequest.PortableInvocationRequest invocationRequest, SecurityZone securityZone);
    }

    public sealed partial class WorkerServer : MarshalByRefObject, IWorkerServer {
        private readonly object stateLock = new object();
        private readonly string serverId = "[server:" + Process.GetCurrentProcess().Id + "]";
        private readonly ClientSponsor proxyLifetimeSponsor = new ClientSponsor();
        private volatile bool isReady = true;
        private volatile TaskRunner activeRunner;
        private volatile AppDomain activeRunnerDomain;
        private volatile CancellationTokenSource cleanupTaskCts;

        public event EventHandler<TaskSucceededEventArgs> TaskSucceededEvent;
        public event EventHandler<TaskCanceledEventArgs> TaskCanceledEvent;
        public event EventHandler<TaskFailedEventArgs> TaskFailedEvent;
        public event EventHandler ServerDyingEvent;
        public event EventHandler ServerReadyEvent;


        public void Cancel(
            TimeSpan cancelTimeout, ForcedCancellationMode forcedCancellationMode
        ) {
            if (forcedCancellationMode == ForcedCancellationMode.KillImmediately) {
                this.CommitSuicide();
            }
            
            lock (this.stateLock) {
                if (this.activeRunner != null && this.cleanupTaskCts == null) {
                    this.cleanupTaskCts = new CancellationTokenSource();
                    
                    // ReSharper disable once UnusedVariable
                    var ensureCanceledTask = this.EnsureCanceled(
                        cancelTimeout, forcedCancellationMode, this.cleanupTaskCts.Token);
                    
                    this.activeRunner.Cancel();
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
                    invocationRequest.MethodName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                // ReSharper disable once AssignNullToNotNullAttribute
                this.activeRunnerDomain = AppDomain.CreateDomain(taskDomainName, AppDomain.CurrentDomain.Evidence,
                    new AppDomainSetup {
                        ApplicationBase = invocationRequest.ApplicationBase,
                        LoaderOptimization = LoaderOptimization.MultiDomainHost
                    }, zonePermissions, typeof(WorkerServer).GetStrongNameOfAssemblyAsArray());

                this.activeRunner = (TaskRunner) this.activeRunnerDomain.CreateInstanceFromAndUnwrap(
                    typeof(TaskRunner).Assembly.Location,typeof(TaskRunner).FullName);
                this.proxyLifetimeSponsor.Register(this.activeRunner);
            }

            Task.Run(() => {
                // this invocation can fail by to ways:
                // 1. forced shutdown of appdomain - ignore silently
                // 2. serialization exception related to type of result - pass to callee
                try {
                    var result = this.activeRunner.InvokeSynchronously(invocationRequest);
                    this.OnRunnerDone(result);
                } catch (SerializationException e) {
                    this.OnRunnerDone(new TaskFailedEventArgs(e));
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
                    e => e(this, (TaskSucceededEventArgs) result),
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


        private async Task EnsureCanceled(
            TimeSpan cancelTimeout, ForcedCancellationMode forcedCancellationMode, CancellationToken ct
        ) {
            if (forcedCancellationMode == ForcedCancellationMode.KillImmediately) {
                this.CommitSuicide();
            }
            
            var asyncCancellation = forcedCancellationMode == ForcedCancellationMode.CleanupAfterCancellation;
            var halfCancelTimeout = TimeSpan.FromMilliseconds(cancelTimeout.TotalMilliseconds / 2);

            try {
                await Task.Delay(asyncCancellation ? cancelTimeout : halfCancelTimeout, ct).ConfigureAwait(false);
            } catch (TaskCanceledException) {
                return;
            }

            if (!asyncCancellation) {
                // ReSharper disable once UnusedVariable
                var cleanupTask = this.CleanupWatchdog(halfCancelTimeout, ct);
            }
            
            this.Cleanup(false, asyncCancellation);
        }

        private async Task CleanupWatchdog(TimeSpan timeout, CancellationToken ct) {
            try {
                await Task.Delay(timeout, ct).ConfigureAwait(false);
                if (!this.isReady) {
                    this.CommitSuicide();
                }
            } catch (TaskCanceledException) {
                // cleanup succeeded within timeout
            }
        }

        // unload appdomain
        private void Cleanup(bool cleanShutdown, bool asyncCancellation = true) {
            lock (this.stateLock) {
                if (this.isReady) {
                    // nothing to cleanup - already clean
                    return;
                }

                if (this.activeRunner != null) {
                    this.proxyLifetimeSponsor.Unregister(this.activeRunner);
                    this.activeRunner = null;
                }
            }

            if (!cleanShutdown) {
                Console.WriteLine(
                    this.serverId + " Failed to cancel task. Going to kill the task. Let's tell.");

                if (asyncCancellation) {
                    this.TaskCanceledEvent.InvokeEvent(
                        e => e(this, new TaskCanceledEventArgs(false)));
                }
            }
            
            try {
                AppDomain runnerDomain;
                lock (this.stateLock) {
                    runnerDomain = this.activeRunnerDomain;
                    this.activeRunnerDomain = null;

                    if (runnerDomain == null) {
                        return;
                    }
                }

                Console.WriteLine(this.serverId + " Going to unload the task's AppDomain.");
                AppDomain.Unload(runnerDomain);
                Console.WriteLine(this.serverId + " Done unloading the task's AppDomain.");
                
                if (!cleanShutdown && !asyncCancellation) {
                    this.TaskCanceledEvent.InvokeEvent(
                        e => e(this, new TaskCanceledEventArgs(false)));
                }

                lock (this.stateLock) {
                    // cleanup task has executed!
                    if (this.cleanupTaskCts != null) {
                        this.cleanupTaskCts.Cancel();
                    }
                    
                    this.activeRunnerDomain = null;
                    this.isReady = true;
                }
                
                this.ServerReadyEvent.InvokeEvent(e => e(this, EventArgs.Empty));
            } catch (CannotUnloadAppDomainException ex) {
                Console.WriteLine(this.serverId + " Failed to unload task's AppDomain: " + ex.Message);
                this.CommitSuicide();
            }
        }

        private void CommitSuicide() {
            Console.WriteLine(this.serverId + " Going to kill myself!");

            // kill current worker in the most robust way possible!
            try {
                this.ServerDyingEvent.InvokeEvent(e => e(this, EventArgs.Empty));
                Process.GetCurrentProcess().Kill();
            } catch (Exception exx) {
                Console.WriteLine(this.serverId + " Failed to commit suicide: " + exx.Message);
                Console.WriteLine(exx.StackTrace);
                Console.WriteLine(this.serverId + " Client will have to take care of that!");
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
                this.Cancel(TimeSpan.FromMilliseconds(50), ForcedCancellationMode.CleanupAfterCancellation);
            }
        }
    }
}