using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;

namespace EpiSource.Unblocker.Hosting {
    public sealed class InstallUtilTrampolineAssemblyProvider : AssemblyProvider {
        
        private static readonly IReadOnlyList<string> assemblyDependencies = 
            new List<string> { typeof(WorkerServerHost).Assembly.Location, "System.Configuration.Install.dll" }.AsReadOnly();

        
        private string assemblyPath = null;

        public InstallUtilTrampolineAssemblyProvider(string dynamicBootstrapperLocation, bool noSideBySide=false)
            : base(dynamicBootstrapperLocation, noSideBySide) { }

        protected override string EnsureAvailableInternal() {
            if (this.assemblyPath != null && File.Exists(this.assemblyPath)) {
                return this.assemblyPath;
            }

            var sideBySideSource = CreateSideBySideBootstrapper();
            var sideBySidePath = Path.Combine(
                Path.GetDirectoryName(typeof(UnblockerHost).Assembly.Location),
                sideBySideSource.AssemblyName);
            
            var boundSource = CreateBoundBootstrapper();
            var boundPath = Path.Combine(this.dynamicAssemblyLocation, boundSource.AssemblyName);

            if (!this.noSideBySide && File.Exists(sideBySidePath)) {
                this.assemblyPath = sideBySidePath;
                return this.assemblyPath;
            }
            if (File.Exists(boundPath)) {
                this.assemblyPath = boundPath;
                return this.assemblyPath;
            }

            if (!this.noSideBySide) {
                try {
                    File.Create(sideBySidePath, 1, FileOptions.DeleteOnClose).Dispose();

                    this.assemblyPath = sideBySideSource.Compile(Path.GetDirectoryName(sideBySidePath));
                    return this.assemblyPath;
                } catch (UnauthorizedAccessException) {
                    // continue
                }
            }

            this.assemblyPath = boundSource.Compile(this.dynamicAssemblyLocation);
            return this.assemblyPath;
        }

        // side-by-side: bootstrapper must be installed into the same directory as the host assembly
        protected static AssemblySource CreateSideBySideBootstrapper() {
            var assemblyName = String.Format("{0}-{1}-SxS", formatUnblockerTitle(), typeof(WorkerServerHost).Assembly.GetName().Version);
            return new AssemblySource(createSourceTemplate(""), assemblyName, assemblyDependencies);
        }

        // bootrapper that can be installed anywhere, but is bound to the location
        // of the host assembly at the time of source creation
        protected static AssemblySource CreateBoundBootstrapper() {
            var hostAssembly = typeof(WorkerServerHost).Assembly;
            var hostAssemblyResolver = @"

        static InstallUtilTrampoline() {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
                if (e.Name == @""" + hostAssembly.FullName + @""") {
                    return Assembly.LoadFile(@""" + hostAssembly.Location + @""");
                }

                return null;
            };
        }

";
            var assemblyName = String.Format("{0}-{1}", formatUnblockerTitle(), typeof(WorkerServerHost).Assembly.GetName().Version);
            return new AssemblySource(createSourceTemplate(hostAssemblyResolver), assemblyName, assemblyDependencies);

        }

        private static string formatUnblockerTitle() {
            var hostAssembly = typeof(WorkerServerHost).Assembly;
            
            var unblockerTitle = "EpiSource.Unblocker.InstallUtilTrampoline";
            if (hostAssembly.GetName().Name != "EpiSource.Unblocker") {
                unblockerTitle += "@" + hostAssembly.GetName().Name;
            }
            return unblockerTitle;
        }

        private static string createSourceTemplate(string additionalCode) {
            var hostAssembly = typeof(WorkerServerHost).Assembly;
            var hostClassName = typeof(WorkerServerHost).FullName;

            Expression<Action<string[]>> startMethod = args => WorkerServerHost.Start(args);
            var hostStartName = ((MethodCallExpression) startMethod.Body).Method.Name;

            var unblockerVersion = hostAssembly.GetName().Version;
            var unblockerCopyright = hostAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>().Copyright;

            var unblockerTitle = formatUnblockerTitle();

            return @"
using System;
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle(""" + unblockerTitle + @""")]
[assembly: AssemblyDescription(""Dynamically created .Net InstallUtil entrypoint for EpiSource.Unblocker."")]
[assembly: AssemblyConfiguration(""{hash}"")]
[assembly: AssemblyCompany(""EpiSource"")]
[assembly: AssemblyProduct(""EpiSource.Unblocker"")]
[assembly: AssemblyCopyright(""" + unblockerCopyright + @""")]
[assembly: AssemblyTrademark("""")]
[assembly: AssemblyCulture("""")]
[assembly: ComVisible(false)]

[assembly: AssemblyVersion(""" + unblockerVersion + @""")]
[assembly: AssemblyFileVersion(""" + unblockerVersion + @""")]
[assembly: AssemblyInformationalVersion(""" + unblockerVersion + @"+{hash}"")]

namespace EpiSource.Unblocker.Hosting {
    [RunInstaller(true)]
    public sealed class InstallUtilTrampoline : System.Configuration.Install.Installer {
        " + additionalCode + @"
        public override void Install(IDictionary stateSaver) {
            " + hostClassName + "." + hostStartName + @"(this.Context.Parameters);
        }
    }
}";
        }
    }
}