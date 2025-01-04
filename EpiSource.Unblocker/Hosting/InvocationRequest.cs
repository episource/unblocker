using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;

using EpiSource.Unblocker.Util;

namespace EpiSource.Unblocker.Hosting {

    public interface IInvocationRequest {
        MethodInfo Method { get; }
        object Target { get; }
        IList<object> Arguments { get; }
        bool HasReturnParameter { get; }
        Type ReturnType { get; }
        IPortableInvocationRequest ToPortableInvocationRequest();
        object Invoke(CancellationToken token);
    }

    public interface IInvocationRequest<out TTarget> : IInvocationRequest {
        new TTarget Target { get; }
        
        new void Invoke(CancellationToken token);
    }
    
    public interface IInvocationRequest<out TTarget, out TReturn> : IInvocationRequest<TTarget> {
        new TReturn Invoke(CancellationToken token);
    }

    public static class InvocationRequest {
        [Serializable]
        private struct CancellationTokenMarker { }

        [Serializable]
        private struct TargetTokenMarker { }
        
        public static InvocationRequest<object, TReturn> FromExpression<TReturn>(Expression<Func<CancellationToken, TReturn>> invocation) {
            return InvocationRequest<object, TReturn>.FromExpression(invocation, null);
        }
        
        public static InvocationRequest<TTarget, TReturn> FromExpression<TTarget, TReturn>(Expression<Func<CancellationToken, TReturn>> invocation, TypeReference<TTarget> targetType) {
            return InvocationRequest<TTarget, TReturn>.FromExpression(invocation, default(TTarget));
        }
        
        public static InvocationRequest<TTarget, TReturn> FromExpression<TTarget, TReturn>(Expression<Func<CancellationToken, TTarget, TReturn>> invocation, TTarget target) {
            return InvocationRequest<TTarget, TReturn>.FromExpression(invocation, target);
        }

        public static InvocationRequest<object, object> FromExpression(Expression<Action<CancellationToken>> invocation) {
            return InvocationRequest<object, object>.FromExpression(invocation, null);
        }
        
        public static InvocationRequest<TTarget, object> FromExpression<TTarget>(Expression<Action<CancellationToken>> invocation, TypeReference<TTarget> targetType) {
            return InvocationRequest<TTarget, object>.FromExpression(invocation, default(TTarget));
        }
        
        public static InvocationRequest<TTarget, object> FromExpression<TTarget>(Expression<Action<CancellationToken, TTarget>> invocation, TTarget target) {
            return InvocationRequest<TTarget, object>.FromExpression(invocation, target);
        }
        
    }
    
    [Serializable]
    public sealed partial class InvocationRequest<TTarget, TReturn> : IInvocationRequest<TTarget, TReturn> {
        
        [Serializable]
        private struct CancellationTokenMarker { }

        [Serializable]
        private struct TargetTokenMarker { }
        
        private readonly MethodInfo method;
        private readonly TTarget target;
        private readonly IList<object> args;

        private InvocationRequest(MethodInfo method, TTarget target, IList<object> args) {
            this.method = method;
            this.target = target;
            this.args = new ReadOnlyCollection<object>(args);
        }

        public MethodInfo Method {
            get { return this.method; }
        }

        public TTarget Target {
            get { return this.target; }
        }

        object IInvocationRequest.Target {
            get {
                return this.Target;
            }
        }

        public IList<object> Arguments {
            get { return this.args; }
        }

        public bool HasReturnParameter {
            get {
                return this.Method.ReturnParameter != null;
            }
        }

        public Type ReturnType {
            get {
                return this.Method.ReturnType;
            }
        }

        public IPortableInvocationRequest ToPortableInvocationRequest() {
            return new PortableInvocationRequest(this);
        }

        public TReturn Invoke(CancellationToken token) {
            var argsArray = this.Arguments.Select(arg => {
                if (arg is CancellationTokenMarker) return token;
                return arg is TargetTokenMarker ? this.target : arg;
            }).ToArray();
            
            try {
                return (TReturn)this.Method.Invoke(this.Target, argsArray);
            } catch (TargetInvocationException e) {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        object IInvocationRequest.Invoke(CancellationToken token) {
            return this.Invoke(token);
        }

        void IInvocationRequest<TTarget>.Invoke(CancellationToken token) {
            this.Invoke(token);
        }

        internal static InvocationRequest<TTarget, TReturn> FromExpression(LambdaExpression invocation, TTarget target) {
            if (invocation == null) {
                throw new ArgumentNullException("invocation");
            }

            if (invocation.Parameters.Count() < 1 || invocation.Parameters.Count() > 2) {
                throw new ArgumentException("Invocation lambda must have one or two parameters.", "invocation");
            }

            if (!typeof(CancellationToken).IsAssignableFrom(invocation.Parameters[0].Type)) {
                throw new ArgumentException(
                    "First parameter of invocation lambda must be of type CancellationToken, but was different.",
                    "invocation");
            }

            var methodCall = invocation.Body as MethodCallExpression;
            if (methodCall == null) {
                var unaryBody = invocation.Body as UnaryExpression;
                if (unaryBody != null && unaryBody.NodeType == ExpressionType.Convert) {
                    methodCall = unaryBody.Operand as MethodCallExpression;
                }
            }

            if (methodCall == null) {
                throw new ArgumentException(
                    "Invalid lambda expression. Only simple method invocation supported. E.g. `t => this.exec(...)`.",
                    "invocation");
            }
            
            if (methodCall.Method.ReturnType != typeof(void) && !typeof(TReturn).IsAssignableFrom(methodCall.Method.ReturnType)) {
                throw new ArgumentException("Return parameter cannot be assigned to R", "TReturn");
            }

            var tokenParameter = invocation.Parameters[0];
            var targetParameter = invocation.Parameters.Count > 1 ? invocation.Parameters[1] : null;
            
            var obj = ResolveExpression(methodCall.Object, target, tokenParameter, targetParameter);
            if (targetParameter != null && !(obj is TargetTokenMarker) && !Object.ReferenceEquals(obj, target)) {
                throw new ArgumentException("Target parameter is present, but invocation does not use it as target.", "invocation");
            }
            if (targetParameter == null && obj != null && !(obj is TTarget)) {
                throw new ArgumentException("Invocation target type " + obj.GetType().Name + " is not compatible with required type " + typeof(TTarget).Name + ".", "invocation");
            }
            
            var args = ResolveArguments(methodCall, target, tokenParameter, targetParameter);
            return new InvocationRequest<TTarget, TReturn>(methodCall.Method, invocation.Parameters.Count() > 1 ? target : (TTarget)obj, args);
        }

        private static IList<object> ResolveArguments(
            MethodCallExpression callExpression, object target, ParameterExpression tokenExpression, ParameterExpression targetExpression
        ) {
            var resolvedArgs = new List<object>(callExpression.Arguments.Count);

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var arg in callExpression.Arguments) {
                resolvedArgs.Add(ResolveExpression(arg, target, tokenExpression, targetExpression));
            }
            return resolvedArgs;
        }

        private static object ResolveExpression(
            Expression anyExpression, object target, ParameterExpression tokenExpression, ParameterExpression targetExpression
        ) {
            if (anyExpression == null) {
                return null;
            }

            var asParamExpression = anyExpression as ParameterExpression;
            if (asParamExpression != null)  {
                if (asParamExpression.Name == tokenExpression.Name) {
                    return new CancellationTokenMarker();
                }
                if (asParamExpression.Name == targetExpression.Name) {
                    return new TargetTokenMarker();
                }
                throw new ArgumentException("Expression has unexpected parameter reference. Expression use only CancellationToken and optionally Target parameters.");
            }

            object retVal;
            if (targetExpression == null) {
                var lambda = Expression.Lambda(anyExpression, tokenExpression).Compile();
                retVal = lambda.DynamicInvoke(CancellationToken.None);
            } else {
                var lambda = Expression.Lambda(anyExpression, tokenExpression, targetExpression).Compile();
                retVal = lambda.DynamicInvoke(CancellationToken.None, target);
            }
            
            if (!IsSerializable(retVal)) {
                throw new SerializationException(
                    string.Format("Evaluation result of expression `{0}` ({1} = {2}) is not serializable.",
                        anyExpression, retVal.GetType().FullName, retVal));
            }

            return retVal;
        }

        private static bool IsSerializable(object obj) {
            return obj == null || IsSerializableType(obj.GetType());
        }

        private static bool IsSerializableType(Type t) {
            return  typeof(ISerializable).IsAssignableFrom(t) ||
                    typeof(string).IsAssignableFrom(t)        ||
                    typeof(void) == t                         ||
                    t.IsSerializable                          ||
                    t.IsPrimitive;
        }
    }
}