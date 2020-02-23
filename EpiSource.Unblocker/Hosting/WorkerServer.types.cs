using System;
using System.Threading;

namespace EpiSource.Unblocker.Hosting {
    public partial class WorkerServer {
        // may be used for one invocation only!
        private sealed class TaskRunner : MarshalByRefObject {
            private readonly CancellationTokenSource cts = new CancellationTokenSource();

            public void Setup() {
                // resolve current assembly across load context
                // important if current assembly was loaded from outside the default assembly search path
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
                    if (e.Name == typeof(TaskRunner).Assembly.FullName) {
                        return typeof(TaskRunner).Assembly;
                    }

                    return null;
                };
            }
            
            public void Cancel() {
                this.cts.Cancel();
            }

            public EventArgs InvokeSynchronously(
                InvocationRequest.PortableInvocationRequest portableInvocationRequest
            ) {
                // Important: Calling parent.OnRunner* causes the app domain executing the current runner to be unloaded
                try {
                    this.cts.Token.ThrowIfCancellationRequested();
                    var result = portableInvocationRequest.ToInvocationRequest().Invoke(this.cts.Token);
                    return new TaskSucceededEventArgs(result);
                } catch (OperationCanceledException e) {
                    if (e.CancellationToken == this.cts.Token) {
                        return new TaskCanceledEventArgs(true);
                    } 
                        
                    return new TaskFailedEventArgs(e);
                } catch (Exception e) {
                    return new TaskFailedEventArgs(e);
                }
            }
        }
    }
}