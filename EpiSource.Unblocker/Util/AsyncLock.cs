using System;
using System.Threading;
using System.Threading.Tasks;

namespace EpiSource.Unblocker.Util {
    public class AsyncLock {

        private readonly SemaphoreSlim mutex = new SemaphoreSlim(1, 1);

        public IDisposable Lock() {
            this.mutex.Wait();
            return new AsyncLockToken(this);
        }
        
        public async Task<IDisposable> LockAsync(CancellationToken ct = default(CancellationToken)) {
            await this.mutex.WaitAsync(ct);
            return new AsyncLockToken(this);
        }

        public IDisposable LockOptional(bool lockAcquired) {
            if (lockAcquired) {
                return new AsyncLockToken(null);
            }
            return this.Lock();
        }
        
        public async Task<IDisposable> LockOptionalAsync(bool lockAcquired, CancellationToken ct = default(CancellationToken)) {
            if (lockAcquired) {
                return new AsyncLockToken(null);
            }
            return await this.LockAsync(ct);
        }

        private class AsyncLockToken : IDisposable {
            private AsyncLock asyncLock;
            public AsyncLockToken(AsyncLock asyncLock) {
                this.asyncLock = asyncLock;
            }
            public void Dispose() {
                if (this.asyncLock != null) {
                    this.asyncLock.mutex.Release();
                    this.asyncLock = null;
                }
            }
        }
        
    }
}