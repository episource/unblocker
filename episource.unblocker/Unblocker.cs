using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

using episource.unblocker.hosting;

namespace episource.unblocker {
    public sealed class Unblocker : IDisposable {
        private static readonly TimeSpan CleanupDelay = TimeSpan.FromMilliseconds(250);
        
        private readonly object stateLock = new object();
        private readonly int maxIdleWorkers;
        private readonly Queue<WorkerClient> idleClients = new Queue<WorkerClient>();
        private readonly LinkedList<WorkerClient> busyClients = new LinkedList<WorkerClient>();
        private readonly DebugMode debugMode;

        private bool disposed;

        public Unblocker(int maxIdleWorkers = 1, DebugMode debug = DebugMode.None) {
            this.maxIdleWorkers = maxIdleWorkers;
            this.debugMode = debug;
        }
        
        public async Task<T> InvokeAsync<T>(
            Expression<Func<CancellationToken, T>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null, SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            try {
                return await this.ActivateWorker()
                                       .InvokeRemotely(invocation, ct, cancellationTimeout, securityZone)
                                       .ConfigureAwait(false);
            } finally {
                this.Cleanup();
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
                await this.ActivateWorker().InvokeRemotely(invocation, ct, cancellationTimeout, securityZone);
            } finally {
                this.Cleanup();
            }
        }

        // releases all idle workers
        public void Standby() {
            lock (this.stateLock) {
                foreach (var client in this.idleClients) {
                    client.Dispose();
                }
                this.idleClients.Clear();
            }
        }

        private WorkerClient ActivateWorker() {
            if (this.disposed) {
                throw new ObjectDisposedException("This instance has been disposed.");
            }
            
            lock (this.stateLock) {
                this.RecoverWorkers();
                var nextClient = this.idleClients.Count > 0 
                    ? this.idleClients.Dequeue() 
                    : new WorkerProcess().Start(this.debugMode);
                this.busyClients.AddLast(nextClient);

                this.EnsureWorkerLimit();
                return nextClient;
            }
        }

        private async void Cleanup() {
            await Task.Delay(CleanupDelay).ConfigureAwait(false);
            
            lock (this.stateLock) {
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
                            break;
                        case WorkerClient.State.Dying:
                        case WorkerClient.State.Dead:
                            if (this.debugMode != DebugMode.None) {
                                Console.WriteLine("Disposing dying/dead worker: " + worker);
                            }
                            this.busyClients.Remove(curNode);
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
                        Console.WriteLine("Disposing surplus worker: " + worker);
                    }
                    
                    worker.Dispose();
                }
            }
        }

        public void Dispose() {
            this.Dispose(true);
        }

        protected /*virtual*/ void Dispose(bool disposing) {
            if (disposing && !this.disposed) {
                this.disposed = true;
                
                lock (this.stateLock) {
                    foreach (var client in this.idleClients) {
                        client.Dispose();
                    }
                    this.idleClients.Clear();

                    foreach (var client in this.busyClients) {
                        client.Dispose();
                    }
                    this.busyClients.Clear();
                }
            }
        }
    }
}