using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

using EpiSource.Unblocker.Hosting;
using EpiSource.Unblocker.Tasks;

namespace EpiSource.Unblocker {
    public sealed class UnblockerHost : IDisposable {
        private static readonly TimeSpan defaultStandbyDelay = TimeSpan.FromMilliseconds(10000);
        private static readonly TimeSpan builtinDefaultCancellationTimeout = TimeSpan.FromMilliseconds(50);

        private readonly object stateLock = new object();
        private readonly Queue<WorkerClient> idleClients = new Queue<WorkerClient>();
        private readonly LinkedList<WorkerClient> busyClients = new LinkedList<WorkerClient>();
        private readonly string id;
        private readonly SemaphoreSlim waitForWorkerSemaphore;
        private readonly int maxIdleWorkers;
        private readonly DebugMode debugMode;
        private readonly CountdownTask standbyTask;
        private readonly TimeSpan defaultCancellationTimeout;

        private volatile bool disposed;

        public UnblockerHost(
            int maxIdleWorkers = 1, int? maxWorkers = null, TimeSpan? standbyDelay = null,
            TimeSpan? defaultCancellationTimeout = null, DebugMode debug = DebugMode.None
        ) {
            this.id = "[unblocker:" + this.GetHashCode() + "]";
            this.waitForWorkerSemaphore = new SemaphoreSlim(
                maxWorkers.GetValueOrDefault(int.MaxValue), maxWorkers.GetValueOrDefault(int.MaxValue));
            
            this.maxIdleWorkers = maxIdleWorkers;
            this.debugMode = debug;
            
            this.standbyTask = new CountdownTask(standbyDelay ?? defaultStandbyDelay, this.Standby);

            this.defaultCancellationTimeout =
                defaultCancellationTimeout.GetValueOrDefault(builtinDefaultCancellationTimeout);
        }

        public async Task<T> InvokeAsync<T>(
            Expression<Func<CancellationToken, T>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            return (T) await handle.InvocationTask.ConfigureAwait(false);
        }
        
        public async Task InvokeAsync(
            Expression<Action<CancellationToken>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            await handle.InvocationTask.ConfigureAwait(false);
        }

        public async Task<UnblockerInvocationHandle<T>> InvokeDetailedAsync<T>(
            Expression<Func<CancellationToken, T>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            if (this.disposed) {
                throw new ObjectDisposedException("This instance has been disposed.");
            }
            
            try {
                this.standbyTask.Cancel();
                
                var nextWorker =  await this.ActivateWorker(ct);
                return nextWorker.InvokeRemotely(invocation, ct,
                    cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout),
                    forcedCancellationMode, securityZone);
            } finally {
                this.standbyTask.Reset();
            }
            
        }

        public async Task<UnblockerInvocationHandle> InvokeDetailedAsync(
            Expression<Action<CancellationToken>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            if (this.disposed) {
                throw new ObjectDisposedException("This instance has been disposed.");
            }

            try {
                this.standbyTask.Cancel();

                var nextWorker =  await this.ActivateWorker(ct);
                return nextWorker.InvokeRemotely(invocation, ct,
                    cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout),
                    forcedCancellationMode, securityZone);
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

        private async Task<WorkerClient> ActivateWorker(CancellationToken ct) {
            if (this.disposed) {
                throw new ObjectDisposedException("This instance has been disposed.");
            }

            if (this.debugMode != DebugMode.None) {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} Waiting for worker to be available.", this.id));
            }
            
            await this.waitForWorkerSemaphore.WaitAsync(ct);
            
            #if !useInstallUtil
            await BootstrapAssemblyProvider.Instance.EnsureAvailableAsync();
            #endif
            
            lock (this.stateLock) {
                this.RecoverWorkers();

                WorkerClient nextClient = null;

                while (nextClient == null && this.idleClients.Count > 0) {
                    nextClient = this.idleClients.Dequeue().EnsureAlive();
                }
                
                if (nextClient == null) {
                    try {
                        nextClient = new WorkerProcess().Start(this.debugMode);
                    } catch {
                        this.waitForWorkerSemaphore.Release();
                        throw;
                    }
                    
                    nextClient.CurrentStateChangedEvent += this.OnWorkerCurrentStateChanged;
                }
                this.busyClients.AddLast(nextClient);

                this.EnsureIdleWorkerLimit();
                
                if (this.debugMode != DebugMode.None) {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0} Successfully retrieved worker {1}", this.id, nextClient));
                }
                return nextClient;
            }
        }

        private void OnParentProcessExit(object sender, EventArgs e) {
            this.Dispose();
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
                this.EnsureIdleWorkerLimit();
            }
        }

        private void RecoverWorkers() {
            lock (this.stateLock) {
                var recoveredWorkerCount = 0;
                var curNode = this.busyClients.First;

                try {
                    while (curNode != null) {
                        var nextNode = curNode.Next;
                        var worker = curNode.Value;
                        
                        worker.EnsureAlive();

                        switch (worker.CurrentState) {
                            case WorkerClient.State.Idle:
                                recoveredWorkerCount++;

                                this.busyClients.Remove(curNode);
                                this.idleClients.Enqueue(worker);

                                break;
                            case WorkerClient.State.Busy:
                            case WorkerClient.State.Cleanup:
                                break;
                            case WorkerClient.State.Dead:
                                recoveredWorkerCount++;

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
                } finally {
                    if (recoveredWorkerCount > 0) {
                        this.waitForWorkerSemaphore.Release(recoveredWorkerCount);
                    }
                }
            }
        }

        private void EnsureIdleWorkerLimit() {
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
                
                AppDomain.CurrentDomain.ProcessExit -= this.OnParentProcessExit;
                
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