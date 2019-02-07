using System;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Runtime.Remoting.Lifetime;
using System.Security;
using System.Threading;
using System.Threading.Tasks;


namespace episource.unblocker.hosting {
    public sealed class WorkerClient : MarshalByRefObject, IDisposable{
        public enum State {
            Idle,
            Busy,
            Cleanup,
            Dying,
            Dead,
        }
        
        // task gets killed if it can't be cancelled within this time
        private static readonly TimeSpan DefaultCancellationTimeout = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan TestifyServerDeathWatchdog = DefaultCancellationTimeout;

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

            this.serverProxy.ServerDyingEvent += this.OnServerDying;
            this.serverProxy.ServerReadyEvent += this.OnServerReady;
            this.serverProxy.TaskFailedEvent += this.OnRemoteTaskFailed;
            this.serverProxy.TaskCanceledEvent += this.OnRemoteTaskCanceled;
            this.serverProxy.TaskSucceededEvent += this.OnRemoteTaskSucceeded;
        }

        public State CurrentState {
            get {
                lock (this.stateLock) {
                    return this.state;
                }
            }
        }
        
        public async Task<T> InvokeRemotely<T>(
            Expression<Func<CancellationToken, T>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null, SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var request = InvocationRequest.FromExpression(invocation);
            return (T) await this.InvokeRemotely(request, ct,
                                     cancellationTimeout.GetValueOrDefault(DefaultCancellationTimeout),
                                     securityZone)
                                 .ConfigureAwait(false);
        }

        public async Task InvokeRemotely(
            Expression<Action<CancellationToken>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null, SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var request = InvocationRequest.FromExpression(invocation);
            await this.InvokeRemotely(request, ct, 
                          cancellationTimeout.GetValueOrDefault(DefaultCancellationTimeout), securityZone)
                      .ConfigureAwait(false);
        }

        public override string ToString() {
            return this.id;
        }

        private Task<object> InvokeRemotely(
            InvocationRequest request, CancellationToken ct, TimeSpan cancellationTimeout, SecurityZone securityZone
        ) {
            lock (this.stateLock) {               
                if (this.CurrentState != State.Idle) {
                    throw new InvalidOperationException(
                        "Worker process is not ready. Current state is: " + this.CurrentState);
                }

                if (!this.process.IsAlive) {
                    this.OnServerDying(this, EventArgs.Empty);
                    throw new InvalidOperationException("Worker process not alive / crashed.");
                }
                
                
                this.activeTcs = new TaskCompletionSource<object>();
                
                // this is the latest time to check whether the task has already been cancelled, before actually
                // starting the task!
                if (ct.IsCancellationRequested) {
                    this.activeTcs.TrySetCanceled();
                    this.activeTcs = null;
                    return this.activeTcs.Task;
                }
                
                this.state = State.Busy;
            }

            ct.Register(() => this.serverProxy.Cancel(cancellationTimeout));
            this.serverProxy.InvokeAsync(request.ToPortableInvocationRequest(), securityZone);

            // Calling Cancel(..) on the server is only handled if there's a invocation request being handled!
            // there's the chance that task was canceled before it was actually started. It might have happened
            // before registering the cancel callback, as well.
            // At this point we now for sure, that the task has been started!
            if (ct.IsCancellationRequested) {
                this.serverProxy.Cancel(cancellationTimeout);
            }
            
            return this.activeTcs.Task;
        }

        private void OnRemoteTaskCanceled(object sender, EventArgs args) {
            lock (this.stateLock) {
                this.state = State.Cleanup;
                this.activeTcs.TrySetCanceled();
                this.activeTcs = null;
            }
        }
        private void OnRemoteTaskSucceeded(object sender, TaskSucceededEventArgs args) {
            lock (this.stateLock) {
                this.state = State.Cleanup;
                this.activeTcs.TrySetResult(args.Result);
                this.activeTcs = null;
            }
        }

        private void OnRemoteTaskFailed(object sender, TaskFailedEventArgs args) {
            lock (this.stateLock) {
                this.state = State.Cleanup;
                this.activeTcs.TrySetException(args.Exception);
                this.activeTcs = null;
            }
        }

        private void OnServerDying(object sender, EventArgs e) {
            lock (this.stateLock) {
                this.state = State.Dying;

                if (this.activeTcs != null) {
                    this.activeTcs.TrySetCanceled();
                    this.activeTcs = null;
                }

                this.TestifyServerDeath();
            }
        }

        private void OnServerReady(object sender, EventArgs e) {
            lock (this.stateLock) {
                this.state = State.Idle;

                // should never happen - nevertheless give the best to handle this
                if (this.activeTcs != null) {
                    this.activeTcs.TrySetCanceled();
                    this.activeTcs = null;
                }
            }
        }

        private async void TestifyServerDeath() {
            await Task.Delay(TestifyServerDeathWatchdog).ConfigureAwait(false);
            this.Dispose();
        }

        public void Dispose() {
            lock (this.stateLock) {
                this.Dispose(true);
            }
        }

        protected /*virtual*/ void Dispose(bool disposing) {
            if (disposing && this.state != State.Dead) {
                this.state = State.Dead;
                
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