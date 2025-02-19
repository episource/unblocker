using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

using EpiSource.Unblocker.Hosting;
using EpiSource.Unblocker.Tasks;
using EpiSource.Unblocker.Util;

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
        
        #region InvokeAsync

        public async Task<TReturn> InvokeAsync<TReturn>(
            Expression<Func<CancellationToken, TReturn>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            return await handle.PlainResult.AsAwaitable().ConfigureAwait(false);
        }
        
        public async Task InvokeAsync(
            Expression<Action<CancellationToken>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            await handle.WaitAsync();
        }
        
        public async Task InvokeAsync(
            IInvocationRequest invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            await handle.WaitAsync();
        }
        
        public async Task<TReturn> InvokeAsync<TReturn, TTarget>(
            IInvocationRequest<TTarget, TReturn> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            return (await handle.FunctionInvocationResult.AsAwaitable()).Result;
        }
        
        #endregion
        
        #region InvokeMutableAsync
        
        public async Task<IMethodInvocationResult<TTarget>> InvokeMutableAsync<TTarget>(TTarget target, Expression<Action<CancellationToken, TTarget>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer) {
            var handle = await this.InvokeDetailedAsync(target, invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            return await handle.MethodInvocationResult.AsAwaitable().ConfigureAwait(false);
        }
        
        public async Task<IMethodInvocationResult<TTarget>> InvokeMutableAsync<TTarget>(
            Expression<Action<CancellationToken>> invocation, TypeReference<TTarget> targetType, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, targetType, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            return await handle.MethodInvocationResult.AsAwaitable().ConfigureAwait(false);
        }
        
        public async Task<IFunctionInvocationResult<TTarget, TReturn>> InvokeMutableAsync<TReturn, TTarget>(TTarget target, Expression<Func<CancellationToken, TTarget, TReturn>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer) {
            var handle = await this.InvokeDetailedAsync(target, invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            return await handle.FunctionInvocationResult.AsAwaitable().ConfigureAwait(false);
        }
        
        public async Task<IFunctionInvocationResult<TTarget, TReturn>> InvokeMutableAsync<TReturn, TTarget>(
            Expression<Func<CancellationToken, TReturn>> invocation, TypeReference<TTarget> targetType, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, targetType, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            return await handle.FunctionInvocationResult.AsAwaitable().ConfigureAwait(false);
        }
        
        public async Task<IFunctionInvocationResult<TTarget, TReturn>> InvokeMutableAsync<TReturn, TTarget>(
            IInvocationRequest<TTarget, TReturn> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            return await handle.FunctionInvocationResult.AsAwaitable().ConfigureAwait(false);
        }
        
        public async Task<IMethodInvocationResult<TTarget>> InvokeMutableAsync<TTarget>(
            IInvocationRequest<TTarget> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            var handle = await this.InvokeDetailedAsync(invocation, ct, cancellationTimeout, forcedCancellationMode, securityZone).ConfigureAwait(false);
            return await handle.MethodInvocationResult.AsAwaitable().ConfigureAwait(false);
        }
        
        #endregion
        
        #region InvokeDetailedAsync

        public async Task<IFunctionInvocationHandle<object, TReturn>> InvokeDetailedAsync<TReturn>(
            Expression<Func<CancellationToken, TReturn>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            return await this.InvokeDetailedImplAsync(ct, w =>
                w.InvokeRemotely(invocation, ct, cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout), forcedCancellationMode, securityZone));
        }
        
        public async Task<IFunctionInvocationHandle<TTarget, TReturn>> InvokeDetailedAsync<TReturn, TTarget>(TTarget target, Expression<Func<CancellationToken, TTarget, TReturn>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer) {
            return await this.InvokeDetailedImplAsync(ct, w =>
                w.InvokeRemotely(invocation, target, ct, cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout), forcedCancellationMode, securityZone));
        }
        
        public async Task<IFunctionInvocationHandle<TTarget, TReturn>> InvokeDetailedAsync<TReturn, TTarget>(
            Expression<Func<CancellationToken, TReturn>> invocation, TypeReference<TTarget> targetType, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            return await this.InvokeDetailedImplAsync(ct, w =>
                w.InvokeRemotely(invocation, targetType, ct, cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout), forcedCancellationMode, securityZone));
        }
        
        public async Task<IMethodInvocationHandle<object>> InvokeDetailedAsync(
            Expression<Action<CancellationToken>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            return await this.InvokeDetailedImplAsync(ct, w =>
                w.InvokeRemotely(invocation, ct, cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout), forcedCancellationMode, securityZone));
        }

        public async Task<IMethodInvocationHandle<TTarget>> InvokeDetailedAsync<TTarget>(TTarget target, Expression<Action<CancellationToken, TTarget>> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer) {
            return await this.InvokeDetailedImplAsync(ct, w =>
                w.InvokeRemotely(invocation, target, ct, cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout), forcedCancellationMode, securityZone));
        }
        
        public async Task<IMethodInvocationHandle<TTarget>> InvokeDetailedAsync<TTarget>(
            Expression<Action<CancellationToken>> invocation, TypeReference<TTarget> targetType, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            return await this.InvokeDetailedImplAsync(ct, w =>
                w.InvokeRemotely(invocation, targetType, ct, cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout), forcedCancellationMode, securityZone));
        }
        
        public async Task<IFunctionInvocationHandle<TTarget, TReturn>> InvokeDetailedAsync<TReturn, TTarget>(
            IInvocationRequest<TTarget, TReturn> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            return await this.InvokeDetailedImplAsync(ct, w =>
                w.InvokeRemotely(invocation, ct, cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout), forcedCancellationMode, securityZone));
        }
        
        public async Task<IMethodInvocationHandle<TTarget>> InvokeDetailedAsync<TTarget>(
            IInvocationRequest<TTarget> invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            return await this.InvokeDetailedImplAsync(ct, w =>
                w.InvokeRemotely(invocation, ct, cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout), forcedCancellationMode, securityZone));
        }
        
        public async Task<IInvocationHandle> InvokeDetailedAsync(
            IInvocationRequest invocation, CancellationToken ct = new CancellationToken(),
            TimeSpan? cancellationTimeout = null,
            ForcedCancellationMode forcedCancellationMode = ForcedCancellationMode.CleanupAfterCancellation,
            SecurityZone securityZone = SecurityZone.MyComputer
        ) {
            return await this.InvokeDetailedImplAsync(ct, w =>
                w.InvokeRemotely(invocation, ct, cancellationTimeout.GetValueOrDefault(this.defaultCancellationTimeout), forcedCancellationMode, securityZone));
        }
        
        #endregion

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

        private async Task<H> InvokeDetailedImplAsync<H>(CancellationToken ct, Func<WorkerClient, H> workerInvocation) {
            if (this.disposed) {
                throw new ObjectDisposedException("This instance has been disposed.");
            }

            try {
                this.standbyTask.Cancel();

                var nextWorker =  await this.ActivateWorker(ct);
                return workerInvocation.Invoke(nextWorker);
            } finally {
                this.standbyTask.Reset();
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