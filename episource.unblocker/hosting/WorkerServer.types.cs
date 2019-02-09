using System;
using System.Threading;


namespace episource.unblocker.hosting {
    public partial class WorkerServer {
        // may be used for one invocation only!
        private sealed class TaskRunner : MarshalByRefObject {
            private readonly CancellationTokenSource cts = new CancellationTokenSource();
            private volatile bool unloadScheduled;

            public void Cancel() {
                this.cts.Cancel();
            }

            public EventArgs InvokeSynchronously(
                WorkerServer parent, InvocationRequest.PortableInvocationRequest portableInvocationRequest
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