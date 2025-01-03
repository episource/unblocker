using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;

namespace EpiSource.Unblocker.Hosting {
    [Serializable]
    public sealed partial class InvocationRequest {
        private readonly MethodInfo method;
        private readonly object target;
        private readonly IList<object> args;

        private InvocationRequest(MethodInfo method, object target, IList<object> args) {
            this.method = method;
            this.target = target;
            this.args = new ReadOnlyCollection<object>(args);
        }

        public MethodInfo Method {
            get { return this.method; }
        }

        public object Target {
            get { return this.target; }
        }

        public IList<object> Arguments {
            get { return this.args; }
        }

        public PortableInvocationRequest ToPortableInvocationRequest() {
            return new PortableInvocationRequest(this);
        }

        public object Invoke(CancellationToken token) {
            var argsArray = this.Arguments.Select(arg => arg is CancellationTokenMarker ? token : arg).ToArray();
            try {
                return this.Method.Invoke(this.Target, argsArray);
            } catch (TargetInvocationException e) {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
        
        public static InvocationRequest FromExpression<T>(Expression<Func<CancellationToken, T>> invocation) {
            return FromExpression((LambdaExpression) invocation);
        }

        public static InvocationRequest FromExpression(Expression<Action<CancellationToken>> invocation) {
            return FromExpression((LambdaExpression) invocation);
        }

        private static InvocationRequest FromExpression(LambdaExpression invocation) {
            if (invocation == null) {
                throw new ArgumentNullException("invocation");
            }

            if (invocation.Parameters.Count != 1 
                    || !typeof(CancellationToken).IsAssignableFrom(invocation.Parameters[0].Type)) {
                throw new ArgumentException(
                    "Only parameter of invocation lambda must be of type CancellationToken, but was different.",
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

            var obj = ResolveExpression(methodCall.Object, invocation.Parameters[0]);
            var args = ResolveArguments(methodCall, invocation.Parameters[0]);
            return new InvocationRequest(methodCall.Method, obj, args);
        }

        private static IList<object> ResolveArguments(
            MethodCallExpression callExpression, ParameterExpression tokenExpression
        ) {
            var resolvedArgs = new List<object>(callExpression.Arguments.Count);

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var arg in callExpression.Arguments) {
                resolvedArgs.Add(ResolveExpression(arg, tokenExpression));
            }
            return resolvedArgs;
        }

        private static object ResolveExpression(
            Expression anyExpression, ParameterExpression tokenExpression
        ) {
            if (anyExpression == null) {
                return null;
            }

            var lambda = Expression.Lambda(anyExpression, tokenExpression).Compile();

            var obj = lambda.DynamicInvoke(new CancellationToken());
            if (obj is CancellationToken) {
                return new CancellationTokenMarker();
            }
            if (!IsSerializable(obj)) {
                throw new SerializationException(
                    string.Format("Evaluation result of expression `{0}` ({1} = {2}) is not serializable.",
                        anyExpression, obj.GetType().FullName, obj));
            }

            return obj;
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