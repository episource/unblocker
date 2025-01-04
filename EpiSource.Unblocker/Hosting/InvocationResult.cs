using System;

namespace EpiSource.Unblocker.Hosting {

    public interface IInvocationResult<out TTarget> {
        /// <summary>
        /// The target state from the worker executing the task. The unblocker worker is operating on an independent clone of the
        /// (serializable) target instance. Therefor state changes are not reflected by the client's target instance. The final state
        /// after the invocation was executed by the worker is stored in this property.
        /// </summary>
        /// <returns>
        /// State of the invocation target after the invocation was executed by the unblocker. Always <c>null</c> for static members.  
        /// </returns>
        TTarget PostInvocationTarget { get; }
    }

    public interface IFunctionInvocationResult<out TTarget, out TReturn> : IInvocationResult<TTarget> {
        bool HasResult { get; }
        TReturn Result { get; }
    }

    public interface IMethodInvocationResult<out TTarget> : IInvocationResult<TTarget> {
        
    }
    
    [Serializable]
    public sealed class InvocationResult<TTarget, TReturn>  : IMethodInvocationResult<TTarget>, IFunctionInvocationResult<TTarget, TReturn> {
        private readonly TTarget postInvocationTarget;
        private readonly TReturn result;
        private readonly bool hasResult;

        public InvocationResult(TTarget postInvocationTarget, TReturn result, bool hasResult) {
            this.result = result;
            this.postInvocationTarget = postInvocationTarget;
            this.hasResult = hasResult;
        }

        /// <summary>
        /// The target state from the worker executing the task. The unblocker worker is operating on an independent clone of the
        /// (serializable) target instance. Therefor state changes are not reflected by the client's target instance. The final state
        /// after the invocation was executed by the worker is stored in this property.
        /// </summary>
        /// <returns>
        /// State of the invocation target after the invocation was executed by the unblocker. Always <c>null</c> for static members.  
        /// </returns>
        public TTarget PostInvocationTarget {
            get {
                return this.postInvocationTarget;
            }
        }

        public bool HasResult {
            get {
                return this.hasResult;
            }
        }
        
        public TReturn Result {
            get {
                if (!this.HasResult) {
                    throw new InvalidOperationException("Result has not bee provided.");
                }
                return this.result;
            }
        }

        
    }

    public static class InvocationResultExtensions {
        public static InvocationResult<TTarget, TReturn> CastTo<TTarget, TReturn>(this InvocationResult<object, object> invocationResult) {
            return new InvocationResult<TTarget, TReturn>((TTarget)invocationResult.PostInvocationTarget, (TReturn)invocationResult.Result, invocationResult.HasResult);
        }
    }
}