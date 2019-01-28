using System;

namespace episource.unblocker.hosting {
    [Serializable]
    public sealed class TaskSucceededEventArgs : EventArgs {
        public TaskSucceededEventArgs(object result) {
            this.Result = result;
        }

        public object Result { get; private set; }
    }

    [Serializable]
    public sealed class TaskFailedEventArgs {
        public TaskFailedEventArgs(Exception e) {
            this.Exception = e;
        }

        public Exception Exception { get; private set; }
    }
}