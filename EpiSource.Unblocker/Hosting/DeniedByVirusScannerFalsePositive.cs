using System;

namespace EpiSource.Unblocker.Hosting {
    public class DeniedByVirusScannerFalsePositive : UnauthorizedAccessException {
        public DeniedByVirusScannerFalsePositive(Exception innerException, string filePath)
                : base("Unable to start worker process: the action was denied by the system's virus scanner!", innerException) {
            this.filePath = filePath;
        }

        private readonly string filePath;
        public string FilePath {
            get {
                return this.filePath;
            }
        }
    }
}