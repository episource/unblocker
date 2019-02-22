using System;

namespace episource.unblocker.hosting {
    public class TaskCrashedException : Exception {
        public TaskCrashedException() 
            : base("The worker process executing the current task crashed or was forced to stop.") { }
    }
}