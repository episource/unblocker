using System;
using System.Diagnostics;

namespace episource.unblocker.hosting {
    public sealed class WorkerProcessRef {
        private Process workerProcess;

        public Process WorkerProcess {
            get {
                return this.workerProcess;
            }
            set {
                if (this.workerProcess != null) {
                    throw new InvalidOperationException("Value already set!");
                }

                this.workerProcess = value;
            }
        }
    }
}