using System;

namespace EpiSource.Unblocker.Hosting {
    public class DeniedByVirusScannerFalsePositive : UnauthorizedAccessException {
        public DeniedByVirusScannerFalsePositive(Exception innerException) 
            : base("Unable to start worker process: the action was denied by the system's virus scanner!", innerException) { }
    }
}