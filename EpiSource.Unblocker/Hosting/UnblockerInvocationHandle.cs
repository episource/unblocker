using System;
using System.Diagnostics;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace EpiSource.Unblocker.Hosting {

    public abstract class UnblockerInvocationHandleBase {
        private readonly Process workerProcess;
        private readonly Task<object> invocationTask;
        private readonly CancellationToken cancellationToken;
        private readonly TimeSpan cancellationTimeout;
        private readonly ForcedCancellationMode forcedCancellationMode;
        private readonly SecurityZone securityZone;

        internal UnblockerInvocationHandleBase(Process workerProcess, Task<object> invocationTask,
                CancellationToken cancellationToken, TimeSpan cancellationTimeout,
                ForcedCancellationMode forcedCancellationMode, SecurityZone securityZone) {
            this.workerProcess = workerProcess;
            this.invocationTask = invocationTask;
            this.cancellationToken = cancellationToken;
            this.cancellationTimeout = cancellationTimeout;
            this.forcedCancellationMode = forcedCancellationMode;
            this.securityZone = securityZone;
        }

        internal UnblockerInvocationHandleBase(UnblockerInvocationHandleBase handle) {
            this.workerProcess = handle.workerProcess;
            this.invocationTask = handle.invocationTask;
            this.cancellationToken = handle.cancellationToken;
            this.cancellationTimeout = handle.cancellationTimeout;
            this.forcedCancellationMode = handle.forcedCancellationMode;
            this.securityZone = handle.securityZone;
        }
        
        public Process WorkerProcess {
            get {
                return this.workerProcess;
            }
        }

        public CancellationToken CancellationToken {
            get {
                return this.cancellationToken;
            }
        }

        public TimeSpan CancellationTimeout {
            get {
                return this.cancellationTimeout;
            }
        }
        
        public ForcedCancellationMode ForcedCancellationMode {
            get {
                return this.forcedCancellationMode;
            }
        }

        public SecurityZone SecurityZone {
            get {
                return this.securityZone;
            }
        }

        protected internal Task<object> InvocationTask {
            get {
                return this.invocationTask;
            }
        }
        
        internal UnblockerInvocationHandle<U> CastTo<U>() {
            return new UnblockerInvocationHandle<U>(this);
        }

        internal UnblockerInvocationHandle CastToVoid() {
            return new UnblockerInvocationHandle(this);
        }
    }
    public sealed class UnblockerInvocationHandle<T> : UnblockerInvocationHandleBase {
        internal UnblockerInvocationHandle(Process workerProcess, Task<object> invocationTask,
            CancellationToken cancellationToken, TimeSpan cancellationTimeout,
            ForcedCancellationMode forcedCancellationMode, SecurityZone securityZone)
        : base(workerProcess, invocationTask, cancellationToken, cancellationTimeout, forcedCancellationMode, securityZone) { }
        
        internal UnblockerInvocationHandle(UnblockerInvocationHandleBase handle) : base(handle) { }
        
        public async Task<T> GetInvocationResultAsync() {
            return (T) await this.InvocationTask.ConfigureAwait(false);
        }
    }

    public sealed class UnblockerInvocationHandle : UnblockerInvocationHandleBase {
        
        internal UnblockerInvocationHandle(Process workerProcess, Task<object> invocationTask,
            CancellationToken cancellationToken, TimeSpan cancellationTimeout,
            ForcedCancellationMode forcedCancellationMode, SecurityZone securityZone)
            : base(workerProcess, invocationTask, cancellationToken, cancellationTimeout, forcedCancellationMode, securityZone) { }
        
        internal UnblockerInvocationHandle(UnblockerInvocationHandleBase handle) : base(handle) { }
        
        public async Task GetInvocationResultAsync() {
            await this.InvocationTask.ConfigureAwait(false);
        }

    }
}