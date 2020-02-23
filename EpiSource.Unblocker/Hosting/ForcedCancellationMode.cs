namespace EpiSource.Unblocker.Hosting {
    public enum ForcedCancellationMode {
        /// <summary>
        /// The task is marked cancelled if it didn't respond to the cancellation request within the given timeout.
        /// Any awaiters continue immediately after timeout. Cleanup is then performed asynchronously after forced
        /// cancellation. Cleanup starts with an attempt to stop the AppDomain that used to execute the task. If this
        /// fails within a system defined timeout (usually a couple of seconds), the associated worker process is
        /// killed. Unloading the AppDomain may fail if the task was running native code.
        /// </summary>
        /// <remarks>
        /// This is the default cancellation mode. It fits all tasks known to execute only managed code. It is also
        /// recommend for tasks executing native code, as long as the native code being still alive after forced
        /// cancellation is known not to block any resources or cause other side-effects.
        /// </remarks>
        CleanupAfterCancellation,
        
        /// <summary>
        /// The task is given a timeout of `cancellationTimeout / 2` to fulfill the cancellation request.  If the task
        /// fails to respond in time, an attempt is made to unload the AppDomain executing the task. If that fails
        /// within the other half of the cancellation timeout, the worker process is killed.
        /// The task is marked cancelled when the AppDomain was unloaded successfully or the worker process was killed.
        /// Any awaiters continue not before the task has been fully shutdown.
        /// </summary>
        /// <remarks>
        /// This cancellation mode fits all applications, that require the task to be fully stopped before awaiters
        /// continue executing. This mode should be considered when the task is invoking native code that blocks
        /// shared resources.
        /// </remarks>
        CleanupBeforeCancellation,
        
        /// <summary>
        /// The task is given a timeout of `cancellationTimeout / 2` to fulfill the cancellation request. If the task
        /// fails to respond in time,  the worker process is killed.
        /// Any awaiters continue not before the task has been fully shutdown or the worker process was killed.
        /// </summary>
        /// <remarks>
        /// This cancellation mode fits all applications invoking mostly native code, that are likely to react on
        /// cancellation in time.
        /// </remarks>
        KillOnCancellationTimeout,
        
        /// <summary>
        /// The worker process executing the task is killed immediately on cancellation request. Any awaiters continue
        /// after the process was canceled.
        /// </summary>
        /// <remarks>
        /// This cancellation mode fits all applications invoking only native code and that are known not to react
        /// on cancellation requests.
        /// </remarks>
        KillImmediately
    }
}