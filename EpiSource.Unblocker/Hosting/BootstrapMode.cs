namespace EpiSource.Unblocker.Hosting {
    public enum BootstrapMode {
        /// <summary>
        /// Dynamically create a custom bootstrapper executable for the unblocker process.
        /// The bootstrapper loads the current assembly and runs the WorkerServer.
        /// An attempt is made to create the bootstrapper in the same directory as the assembly
        /// containing the UnblockerHost type. This executable's code does not include a
        /// hard-coded reference to the UnblockerHost assembly.
        /// If that fails, the given dynamicAssemblyLocation is used instead. The generated
        /// executable's code includes a hard-coded reference to the UnblockerHost assembly.
        /// If this assembly is moved, the executable becomes invalid and must be recreated. 
        /// </summary>
        CustomBootstrapper,
        
        /// <summary>
        /// Dynamically create a custom bootstrapper executable for the unblocker process.
        /// The bootstrapper loads the current assembly and runs the WorkerServer.
        /// The bootstrapper is created in the given dynamicAssemblyLocation. The generated
        /// executable's code includes a hard-coded reference to the UnblockerHost assembly.
        /// If this assembly is moved, the executable becomes invalid and must be recreated.
        /// </summary>
        CustomBootstrapperNoSideBySide,
        
        /// <summary>
        /// Dynamically create a custom InstallUtil pseudo-installer assembly is created for
        /// the unblocker process. The .Net framework included InstallUtil is used to load
        /// this assembly and execute the unblocker worker server process.
        /// An attempt is made to create the pseudo-installer in the same directory as the assembly
        /// containing the UnblockerHost type. This assembly does not include a hard-coded reference
        /// to its UnblockerHost dependency.
        /// If that fails, the given dynamicAssemblyLocation is used instead. The generated
        /// assembly's code includes a hard-coded reference to the UnblockerHost dependency.
        /// If this assembly is moved, the generated assembly becomes invalid and must be recreated. 
        /// </summary>
        InstallUtilTrampoline,
        
        /// <summary>
        /// Dynamically create a custom InstallUtil pseudo-installer assembly is created for
        /// the unblocker process. The .Net framework included InstallUtil is used to load
        /// this assembly and execute the unblocker worker server process.
        /// The generated assembly is written to the given dynamicAssemblyLocation. The generated
        /// assembly's code includes a hard-coded reference to the UnblockerHost dependency.
        /// If this assembly is moved, the generated assembly becomes invalid and must be recreated. 
        /// </summary>
        InstallUtilTrampolineNoSideBySide,
        
        #if !noBootstrapModeInstallUtilPlain
        /// <summary>
        /// .Net Framework included InstallUtil is used to start the worker server host directly
        /// from the assembly containing the UnblockerHost. This fails if not all necessary dependencies
        /// of this assembly are either located in GAC or in the same directory.
        /// </summary>
        InstallUtilPlain
        #endif
    }
}