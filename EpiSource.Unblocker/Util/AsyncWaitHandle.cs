using System.Threading;
using System.Threading.Tasks;

namespace EpiSource.Unblocker.Util {
    public static class AsyncWaitHandle {
        public static bool WaitOne(this WaitHandle handle, int millisecondsTimeout, CancellationToken cancellationToken = default(CancellationToken), bool exitContext = true) {
            int n = WaitHandle.WaitAny(new[] {
                handle, cancellationToken.WaitHandle
            }, millisecondsTimeout, exitContext);
            switch (n) {
                case WaitHandle.WaitTimeout:
                    return false;
                case 0:
                    return true;
                default:
                    cancellationToken.ThrowIfCancellationRequested();
                    return false; // never reached
            }
        }

        public static async Task<bool> WaitOneAsync(this WaitHandle handle, int millisecondsTimeout, CancellationToken cancellationToken = default(CancellationToken), bool exitContext = true) {
            RegisteredWaitHandle registeredHandle = null;
            var tokenRegistration = new CancellationTokenRegistration();
            try {
                var tcs = new TaskCompletionSource<bool>();
                registeredHandle = ThreadPool.RegisterWaitForSingleObject(
                    handle,
                    (state, timedOut) => ((TaskCompletionSource<bool>) state).TrySetResult(!timedOut),
                    tcs,
                    millisecondsTimeout,
                    true);
                
                tokenRegistration = cancellationToken.Register(
                    state => ((TaskCompletionSource<bool>) state).TrySetCanceled(),
                    tcs);
                return await tcs.Task;
            } finally {
                if (registeredHandle != null) {
                    registeredHandle.Unregister(handle);
                }
                
                tokenRegistration.Dispose();
            }
        }
    }
}