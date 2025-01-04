using System;
using System.Diagnostics;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

using EpiSource.Unblocker.Hosting;
using EpiSource.Unblocker.Tasks;

namespace EpiSource.Unblocker.Hosting {

    public interface IInvocationHandle {

        IInvocationRequest InvocationRequest { get; }
        
        Process WorkerProcess { get; }
        CancellationToken CancellationToken { get; }
        TimeSpan CancellationTimeout { get; }
        ForcedCancellationMode ForcedCancellationMode { get; }
        SecurityZone SecurityZone { get; }
        
        /// <summary>
        /// Refer to <see cref="IInvocationResult{TTarget}.PostInvocationTarget"/>.
        /// </summary>
        Task<object> PostInvocationTarget { get; }

        Task WaitAsync();
    }

    public interface IFunctionInvocationHandle<out TTarget, out TReturn> : IInvocationHandle {
        new IInvocationRequest<TTarget, TReturn> InvocationRequest { get; }
        ITaskLike<IFunctionInvocationResult<TTarget, TReturn>> FunctionInvocationResult { get; }
        ITaskLike<TReturn> PlainResult { get; }
        
        /// <summary>
        /// Refer to <see cref="IInvocationResult{TTarget}.PostInvocationTarget"/>.
        /// </summary>
        new ITaskLike<TTarget> PostInvocationTarget { get; }
    }

    public interface IMethodInvocationHandle<out TTarget> : IInvocationHandle {
        new IInvocationRequest<TTarget> InvocationRequest { get; }
        ITaskLike<IMethodInvocationResult<TTarget>> MethodInvocationResult { get; }
        
        /// <summary>
        /// Refer to <see cref="IInvocationResult{TTarget}.PostInvocationTarget"/>.
        /// </summary>
        new ITaskLike<TTarget> PostInvocationTarget { get; }
    }

    public sealed class InvocationHandle<TTarget, TReturn> : IFunctionInvocationHandle<TTarget, TReturn>, IMethodInvocationHandle<TTarget> {
        private readonly IInvocationRequest<TTarget, TReturn> invocationRequest;
        private readonly Task<InvocationResult<TTarget, TReturn>> invocationTask;
        private readonly Process workerProcess;
        private readonly CancellationToken cancellationToken;
        private readonly TimeSpan cancellationTimeout;
        private readonly ForcedCancellationMode forcedCancellationMode;
        private readonly SecurityZone securityZone;

        internal InvocationHandle(IInvocationRequest<TTarget, TReturn> invocationRequest,
                Task<InvocationResult<TTarget, TReturn>> invocationTask,
                Process workerProcess,
                CancellationToken cancellationToken, TimeSpan cancellationTimeout,
                ForcedCancellationMode forcedCancellationMode, SecurityZone securityZone) {
            this.invocationRequest = invocationRequest;
            this.invocationTask = invocationTask;
            this.workerProcess = workerProcess;
            this.cancellationToken = cancellationToken;
            this.cancellationTimeout = cancellationTimeout;
            this.forcedCancellationMode = forcedCancellationMode;
            this.securityZone = securityZone;
        }

        public IInvocationRequest<TTarget, TReturn> InvocationRequest { get { return this.invocationRequest; } }
        IInvocationRequest IInvocationHandle.InvocationRequest {
            get {
                return (IInvocationRequest<TTarget, object>)this.invocationRequest;
            }
        }
        IInvocationRequest<TTarget> IMethodInvocationHandle<TTarget>.InvocationRequest {
            get {
                return this.InvocationRequest;
            }
        }

        public Task InvocationTask { get { return this.invocationTask; } }

        public Process WorkerProcess { get { return this.workerProcess; } }

        public CancellationToken CancellationToken { get { return this.cancellationToken; } }

        public TimeSpan CancellationTimeout { get { return this.cancellationTimeout; } }
        
        public ForcedCancellationMode ForcedCancellationMode { get { return this.forcedCancellationMode; } }

        public SecurityZone SecurityZone { get { return this.securityZone; } }

        public ITaskLike<TTarget> PostInvocationTarget {
            get {
                return this.invocationTask.TransformResultAsTaskLike(r => r.PostInvocationTarget);
            }
        }
        Task<object> IInvocationHandle.PostInvocationTarget {
            get {
                return this.invocationTask.TransformResult(r => (object)r.PostInvocationTarget);
            }
        }

        public ITaskLike<TReturn> PlainResult {
            get {
                return this.invocationTask.TransformResultAsTaskLike(r => r.Result);
            }
        }
        
        public ITaskLike<IFunctionInvocationResult<TTarget, TReturn>> FunctionInvocationResult {
            get {
                return this.invocationTask.AsTaskLike();
            }
        }

        public ITaskLike<IMethodInvocationResult<TTarget>> MethodInvocationResult {
            get {
                return this.invocationTask.AsTaskLike();
            }
        }

        public Task WaitAsync() {
            return this.invocationTask;
        }

    }
}