using System;
using System.Threading;


namespace episource.unblocker.hosting {
    public partial class WorkerServer {
        // may be used for one invocation only!
        private sealed class TaskRunner : MarshalByRefObject {
            private readonly CancellationTokenSource cts = new CancellationTokenSource();
            private volatile bool unloadScheduled = false;

            public void Cancel() {
                this.cts.Cancel();
            }

            public void NotifyUnload() {
                this.unloadScheduled = true;
            }

            public void InvokeSynchronously(
                WorkerServer parent, InvocationRequest.PortableInvocationRequest portableInvocationRequest
            ) {
                // Important: Calling parent.OnRunner* causes the app domain executing the current runner to be unloaded
                try {
                    cts.Token.ThrowIfCancellationRequested();
                    var result = portableInvocationRequest.ToInvocationRequest().Invoke(cts.Token);
                    parent.OnRunnerSucceeded(this, result);
                } catch (OperationCanceledException e) {
                    if (e.CancellationToken == this.cts.Token) {
                        parent.OnRunnerCanceled(this);
                    } else {
                        parent.OnRunnerFailed(this, e);
                    }
                } catch (ThreadAbortException e) {
                    // if the appdomain is scheduled for unloading we are already dead and notifying the parent
                    // will be of no use - even worse it will fail unloading the appdomain cleanly.
                    if (!this.unloadScheduled) {
                        parent.OnRunnerFailed(this, e);
                    }
                } catch (Exception e) {
                    parent.OnRunnerFailed(this, e);
                }
            }
        }
    }
}