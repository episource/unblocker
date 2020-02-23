using System;
using System.Threading;
using System.Threading.Tasks;

namespace EpiSource.Unblocker.Tasks {
    public sealed class CountdownTask {
        private readonly TimeSpan countdown;
        private readonly Action<CancellationToken> action;
        private volatile CancellationTokenSource cts;


        public CountdownTask(TimeSpan countdown, Action action)
            : this(countdown, ct => action()) { }

        public CountdownTask(TimeSpan countdown, Action<CancellationToken> action) {
            if (countdown == null) {
                throw new ArgumentNullException("countdown");
            }

            if (action == null) {
                throw new ArgumentNullException("action");
            }

            this.countdown = countdown;
            this.action = action;
        }

        public bool IsRunning {
            get { return this.cts != null; }
        }

        public void Cancel() {
            CancellationTokenSource activeCts;
            
            do {
                activeCts = this.cts;
            } while (Interlocked.CompareExchange(ref this.cts, null, activeCts) != activeCts);
            
            if (activeCts != null) {
                activeCts.Cancel();
                activeCts.Dispose();
            }
        }

        public void Reset() {
            var newCts = new CancellationTokenSource();
            CancellationTokenSource activeCts;
            
            do {
                activeCts = this.cts;
            } while (Interlocked.CompareExchange(ref this.cts, newCts, activeCts) != activeCts);
            
            if (activeCts != null) {
                activeCts.Cancel();
                activeCts.Dispose();
            }

            this.ScheduleAction(newCts, newCts.Token);
        }

        public void Start() {
            if (!this.TryStart()) {
                throw new InvalidOperationException("Countdown already started.");
            }
        }

        public bool TryStart() {
            var newCts = new CancellationTokenSource();
            
            if (Interlocked.CompareExchange(ref this.cts, newCts, null) != null) {
                newCts.Dispose();
                return false;
            }
            
            this.ScheduleAction(newCts, newCts.Token);
            return true;
        }

        // CancellationTokenSource passed together with corresponding CancellationToken:
        // cts might have been disposed already, which would make cts.Token throw!
        // cts still needed for CompareExchange
        // ReSharper disable once ParameterHidesMember
        private async void ScheduleAction(CancellationTokenSource cts, CancellationToken ct) {
            try {
                await Task.Delay(this.countdown, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                this.action(ct);
            } catch (TaskCanceledException e) {
                // prevent expected exception to show up as unhandled AppDomain exception
                if (e.CancellationToken != ct) {
                    throw;
                }
            }

            // new task might already be scheduled => CompareExchange
            Interlocked.CompareExchange(ref this.cts, null, cts);
            cts.Dispose();
        }
    }
}