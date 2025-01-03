using System;
using System.Linq.Expressions;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Lifetime;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace EpiSource.Unblocker.Hosting {
    [Serializable]
    public sealed class WorkerStateChangedEventArgs : EventArgs {
        public WorkerStateChangedEventArgs(WorkerClient.State state) {
            this.State = state;
        }
        
        public WorkerClient.State State { get; private set; }
    }
    
    public sealed class WorkerClient : MarshalByRefObject, IDisposable{
        public enum State {
            Idle,
            Busy,
            Cleanup,
            Dying,
            Dead,
        }
        
        private readonly object stateLock = new object();
        private readonly ClientSponsor proxyLifetimeSponsor = new ClientSponsor();
        private readonly string id;
        private readonly WorkerProcess process;
        private readonly IWorkerServer serverProxy;
        
        private volatile State state = State.Idle;
        private TaskCompletionSource<object> activeTcs;

        public WorkerClient(WorkerProcess process, IWorkerServer serverProxy) {
            this.process = process;
            this.id = "[client:" + process.Id + "]";
            
            this.serverProxy = serverProxy;
            if (this.serverProxy is MarshalByRefObject) {
                this.proxyLifetimeSponsor.Register((MarshalByRefObject)this.serverProxy);
            }

            this.process.ProcessDeadEvent += this.OnProcessDead;

            this.serverProxy.ServerDyingEvent += this.OnServerDying;
            this.serverProxy.ServerReadyEvent += this.OnServerReady;
            this.serverProxy.TaskFailedEvent += this.OnRemoteTaskFailed;
            this.serverProxy.TaskCanceledEvent += this.OnRemoteTaskCanceled;
            this.serverProxy.TaskSucceededEvent += this.OnRemoteTaskSucceeded;
        }

        // Important: don't raise when holding the state lock!
        public event EventHandler<WorkerStateChangedEventArgs> CurrentStateChangedEvent;

        public State CurrentState {
            get {
                return this.state;
            }
        }

        public WorkerClient EnsureAlive() {
            if (this.state == State.Dead) {
                return null;
            }
            if (this.process.IsAlive) return this;
            
            this.OnProcessDead(this, EventArgs.Empty);
            return null;
        }
        
        public UnblockerInvocationHandle<T> InvokeRemotely<T>(
            Expression<Func<CancellationToken, T>> invocation, CancellationToken ct,
            TimeSpan cancellationTimeout, ForcedCancellationMode forcedCancellationMode, SecurityZone securityZone
        ) {
            var request = InvocationRequest.FromExpression(invocation);
            return this.InvokeRemotely(request, ct, cancellationTimeout, forcedCancellationMode, securityZone)
                       .CastTo<T>();
        }

        public UnblockerInvocationHandle InvokeRemotely(
            Expression<Action<CancellationToken>> invocation, CancellationToken ct,
            TimeSpan cancellationTimeout, ForcedCancellationMode forcedCancellationMode,
            SecurityZone securityZone
        ) {
            var request = InvocationRequest.FromExpression(invocation);
            return this.InvokeRemotely(request, ct, cancellationTimeout, forcedCancellationMode, securityZone)
                       .CastToVoid();
        }

        public override string ToString() {
            return this.id;
        }

        private UnblockerInvocationHandle<object> InvokeRemotely(
            InvocationRequest request, CancellationToken ct, TimeSpan cancellationTimeout, 
            ForcedCancellationMode forcedCancellationMode, SecurityZone securityZone
        ) {
            const State nextState = State.Busy;
            
            lock (this.stateLock) {               
                if (this.CurrentState != State.Idle) {
                    throw new InvalidOperationException(
                        "Worker process is not ready. Current state is: " + this.CurrentState);
                }

                if (!this.process.IsAlive) {
                    this.OnProcessDead(this, EventArgs.Empty);
                    throw new InvalidOperationException("Worker process not alive / crashed.");
                }
                
                this.state = nextState;
                this.activeTcs = new TaskCompletionSource<object>();
                
                // this is the latest time to check whether the task has already been cancelled, before actually
                // starting the task!
                if (ct.IsCancellationRequested) {
                    this.activeTcs.TrySetCanceled();
                    this.activeTcs = null;
                    return new UnblockerInvocationHandle<object>(
                        this.process.Process, this.activeTcs.Task, ct, cancellationTimeout, forcedCancellationMode, securityZone);
                }
            }
            
            // outside lock!
            this.OnCurrentStateChanged(nextState);
            
            // FIXME: Dispose registration!
            ct.Register(() => {
                try {
                    this.serverProxy.Cancel(cancellationTimeout, forcedCancellationMode);
                } catch (RemotingException) {
                    if (!this.process.IsAlive) {
                        // worker killed itself or crashed: ignore!
                        return;
                    }

                    throw;
                }
            });
            this.serverProxy.InvokeAsync(request.ToPortableInvocationRequest(), securityZone);

            // Calling Cancel(..) on the server is only handled if there's a invocation request being handled!
            // there's the chance that task was canceled before it was actually started. It might have happened
            // before registering the cancel callback, as well.
            // At this point we now for sure, that the task has been started!
            if (ct.IsCancellationRequested) {
                this.serverProxy.Cancel(cancellationTimeout, forcedCancellationMode);
            }
            
            return new UnblockerInvocationHandle<object>(
                this.process.Process, this.activeTcs.Task, ct, cancellationTimeout, forcedCancellationMode, securityZone);
        }

        // do not hold state lock when invoking this!
        private void OnCurrentStateChanged(State nextState) {
            var handler = this.CurrentStateChangedEvent;
            if (handler != null) {
                handler(this, new WorkerStateChangedEventArgs(nextState));
            }
        }

        private void OnRemoteTaskCanceled(object sender, PortableEventArgs<TaskCanceledEventArgs> args) {
            this.OnRemoteTaskDone(tcs => tcs.TrySetCanceled());
        }
        private void OnRemoteTaskSucceeded(object sender, PortableEventArgs<TaskSucceededEventArgs> args) {
            this.OnRemoteTaskDone(tcs => tcs.TrySetResult(args.Deserialize().Result));
        }

        private void OnRemoteTaskFailed(object sender, PortableEventArgs<TaskFailedEventArgs> args) {
            this.OnRemoteTaskDone(tcs => tcs.TrySetException(args.Deserialize().Exception));
        }

        private void OnRemoteTaskDone(Action<TaskCompletionSource<object>> tcsUpdate) {
            const State nextState = State.Cleanup;
            
            lock (this.stateLock) {
                this.state = nextState;
                
                tcsUpdate(this.activeTcs);
                this.activeTcs = null;
            }
            
            // outside lock!
            this.OnCurrentStateChanged(nextState);
        }

        private void OnServerDying(object sender, EventArgs e) {
            const State nextState = State.Dying;
            
            lock (this.stateLock) {
                this.state = nextState;
            }
            
            // outside lock!
            this.OnCurrentStateChanged(nextState);
        }

        private void OnProcessDead(object sender, EventArgs e) {
            const State nextState = State.Dead;
            
            lock (this.stateLock) {
                if (this.activeTcs != null) {
                    if (this.state == State.Dying) {
                        this.activeTcs.TrySetCanceled();
                    } else {
                        this.activeTcs.TrySetException(new TaskCrashedException());
                    }

                    this.activeTcs = null;
                }
                
                this.state = nextState;
            }
            
            // outside lock!
            this.OnCurrentStateChanged(nextState);
            this.Dispose();
        }

        private void OnServerReady(object sender, EventArgs e) {
            const State nextState = State.Idle;
            
            lock (this.stateLock) {
                // should never happen - nevertheless give the best to handle this
                if (this.activeTcs != null) {
                    this.activeTcs.TrySetCanceled();
                    this.activeTcs = null;
                }
                
                this.state = nextState;
            }
            
            // outside lock!
            this.OnCurrentStateChanged(nextState);
        }

        public void Dispose() {
            this.Dispose(true);
        }

        private /*protected virtual*/ void Dispose(bool disposing) {
            if (disposing && this.state != State.Dead) {
                lock (this.stateLock) {
                    this.state = State.Dead;
                    
                    this.process.ProcessDeadEvent -= this.OnProcessDead;

                    this.serverProxy.ServerDyingEvent -= this.OnServerDying;
                    this.serverProxy.ServerReadyEvent -= this.OnServerReady;
                    this.serverProxy.TaskFailedEvent -= this.OnRemoteTaskFailed;
                    this.serverProxy.TaskCanceledEvent -= this.OnRemoteTaskCanceled;
                    this.serverProxy.TaskSucceededEvent -= this.OnRemoteTaskSucceeded;
                    
                    this.proxyLifetimeSponsor.Close();
                    this.process.Dispose();
                }
            }
        }
    }
}