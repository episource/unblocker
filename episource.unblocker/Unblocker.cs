using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

using episource.unblocker.hosting;
using episource.unblocker.tasks;

namespace episource.unblocker {
    public sealed class Unblocker : IDisposable {
        private static readonly TimeSpan defaultStandbyDelay = TimeSpan.FromMilliseconds(10000);

        private readonly string id;
        private readonly object stateLock = new object();
        private readonly int maxIdleWorkers;
        private readonly Queue<WorkerClient> idleClients = new Queue<WorkerClient>();
        private readonly LinkedList<WorkerClient> busyClients = new LinkedList<WorkerClient>();
        private readonly DebugMode debugMode;
        private readonly CountdownTask standbyTask;

        private volatile bool disposed;

        public Unblocker(
            int maxIdleWorkers = 1, DebugMode debug = DebugMode.None, TimeSpan? standbyDelay = null
        ) {
            this.id = "[unblocker:" + this.GetHashCode() + "]";
            
            this.maxIdleWorkers = maxIdleWorkers;
            this.debugMode = debug;
            
            this.standbyTask = new CountdownTask(standbyDelay ?? defaultStandbyDelay, this.Standby);
        }
        
        public async Task<T> InvokeAsync<T>(
            Expression<Func<CancellationToken, T>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null, SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            if (this.disposed) {
                throw new ObjectDisposedException("This instance has been disposed.");
            }
            
            try {
                this.standbyTask.Cancel();
                
                return await this.ActivateWorker()
                                       .InvokeRemotely(invocation, ct, cancellationTimeout, securityZone)
                                       .ConfigureAwait(false);
            } finally {
                this.standbyTask.Reset();
            }
            
        }

        public async Task InvokeAsync(
            Expression<Action<CancellationToken>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null, SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            if (this.disposed) {
                throw new ObjectDisposedException("This instance has been disposed.");
            }

            try {
                this.standbyTask.Cancel();
                
                await this.ActivateWorker().InvokeRemotely(invocation, ct, cancellationTimeout, securityZone);
            } finally {
                this.standbyTask.Reset();
            }
        }

        // releases all idle workers
        public void Standby() {
            if (this.debugMode != DebugMode.None) {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} Standby started.", this.id));
            }
            
            lock (this.stateLock) {
                this.RecoverWorkers();
                
                foreach (var client in this.idleClients) {
                    client.Dispose();
                }
                this.idleClients.Clear();
            }
        }

        public override string ToString() {
            return this.id;
        }

        private WorkerClient ActivateWorker() {
            if (this.disposed) {
                throw new ObjectDisposedException("This instance has been disposed.");
            }
            
            lock (this.stateLock) {
                this.RecoverWorkers();

                WorkerClient nextClient;
                if (this.idleClients.Count > 0) {
                    nextClient = this.idleClients.Dequeue();
                } else {
                    nextClient = new WorkerProcess().Start(this.debugMode);
                    nextClient.CurrentStateChangedEvent += this.OnWorkerCurrentStateChanged;
                }
                this.busyClients.AddLast(nextClient);

                this.EnsureWorkerLimit();
                return nextClient;
            }
        }

        private void OnWorkerCurrentStateChanged(object sender, WorkerStateChangedEventArgs args) {
            if (args.State == WorkerClient.State.Idle || args.State == WorkerClient.State.Dead) {
                this.Cleanup();
            }
        }

        private void Cleanup() {
            if (this.debugMode != DebugMode.None) {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} Cleanup started.", this.id));
            }
            
            lock (this.stateLock) {
                this.standbyTask.Reset();
                this.RecoverWorkers();
                this.EnsureWorkerLimit();
            }
        }

        private void RecoverWorkers() {
            lock (this.stateLock) {
                var curNode = this.busyClients.First;
                while (curNode != null) {
                    var nextNode = curNode.Next;
                    var worker = curNode.Value;

                    switch (worker.CurrentState) {
                        case WorkerClient.State.Idle:
                            this.busyClients.Remove(curNode);
                            this.idleClients.Enqueue(worker);
                            break;
                        case WorkerClient.State.Busy:
                        case WorkerClient.State.Cleanup:
                        case WorkerClient.State.Dying:
                            break;
                        case WorkerClient.State.Dead:
                            if (this.debugMode != DebugMode.None) {
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "{0} Disposing dead worker: {1}", this.id, worker));
                            }
                            this.busyClients.Remove(curNode);
                            
                            worker.CurrentStateChangedEvent -= this.OnWorkerCurrentStateChanged;
                            worker.Dispose();
                            break;
                        default:
                            throw new InvalidOperationException("Unexpected worker state.");
                    }

                    curNode = nextNode;
                }
            }
        }

        private void EnsureWorkerLimit() {
            lock (this.stateLock) {
                while (this.idleClients.Count > this.maxIdleWorkers) {
                    var worker = this.idleClients.Dequeue();
                    
                    if (this.debugMode != DebugMode.None) {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "{0} Disposing surplus worker: {1}", this.id, worker));
                    }

                    worker.CurrentStateChangedEvent -= this.OnWorkerCurrentStateChanged;
                    worker.Dispose();
                }
            }
        }
        
        #region IDisposable

        public void Dispose() {
            this.Dispose(true);
        }

        private /*protected virtual*/ void Dispose(bool disposing) {
            if (disposing && !this.disposed) {
                this.disposed = true;
                
                lock (this.stateLock) {
                    foreach (var client in this.idleClients) {
                        client.CurrentStateChangedEvent -= this.OnWorkerCurrentStateChanged;
                        client.Dispose();
                    }
                    this.idleClients.Clear();

                    foreach (var client in this.busyClients) {
                        client.CurrentStateChangedEvent -= this.OnWorkerCurrentStateChanged;
                        client.Dispose();
                    }
                    this.busyClients.Clear();
                }
            }
        }
        
        #endregion
    }
}