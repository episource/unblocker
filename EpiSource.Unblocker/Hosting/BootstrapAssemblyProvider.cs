using System;
using System.CodeDom.Compiler;
using System.Dynamic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using EpiSource.Unblocker.Util;

using Microsoft.CSharp;

namespace EpiSource.Unblocker.Hosting {
    public sealed class BootstrapAssemblyProvider {
        
        private readonly SemaphoreSlim semaphoreOneAtATime = new SemaphoreSlim(1, 1);
        private readonly string dynamicBootstrapperLocation;
        private string assemblyPath = null;

        public BootstrapAssemblyProvider(string dynamicBootstrapperLocation) {
            this.dynamicBootstrapperLocation = dynamicBootstrapperLocation ?? Path.GetTempPath();
        }
        
        public async Task<string> EnsureAvailableAsync() {
            await this.semaphoreOneAtATime.WaitAsync();
            try {
                return this.EnsureAvailableInternal();
            } finally {
                this.semaphoreOneAtATime.Release();
            }
        }

        public string EnsureAvailable() {
            this.semaphoreOneAtATime.Wait();
            try {
                return this.EnsureAvailableInternal();
            } finally {
                this.semaphoreOneAtATime.Release();
            }
        }

        private string EnsureAvailableInternal() {
            if (this.assemblyPath != null && File.Exists(this.assemblyPath)) {
                return this.assemblyPath;
            }

            var sideBySideSource = BootstrapAssemblySource.CreateSideBySideBootstrapper();
            var sideBySidePath = Path.Combine(
                Path.GetDirectoryName(typeof(UnblockerHost).Assembly.Location),
                sideBySideSource.AssemblyName);
            
            var boundSource = BootstrapAssemblySource.CreateBoundBootstrapper();
            var boundPath = Path.Combine(this.dynamicBootstrapperLocation, boundSource.AssemblyName);

            if (File.Exists(sideBySidePath)) {
                this.assemblyPath = sideBySidePath;
                return this.assemblyPath;
            }
            if (File.Exists(boundPath)) {
                this.assemblyPath = boundPath;
                return this.assemblyPath;
            }

            try {
                File.Create(sideBySidePath, 1, FileOptions.DeleteOnClose).Dispose();
                
                this.assemblyPath = sideBySideSource.Compile(Path.GetDirectoryName(sideBySidePath));
                return this.assemblyPath;
            } catch (UnauthorizedAccessException) {
                // continue
            }
            
            this.assemblyPath = boundSource.Compile(this.dynamicBootstrapperLocation);
            return this.assemblyPath;
        }

        public class BootstrapAssemblySource {
            public readonly string SourceHash;
            public readonly string Source;
            public readonly string AssemblyName;

            private BootstrapAssemblySource(string source, uint sourceHash, string assemblyName) {
                this.Source = source;
                this.SourceHash = String.Format("0x{0:x8}", sourceHash);;
                this.AssemblyName = assemblyName;
            }

            public string Compile(string outputDirectoryPath) {
                var provider = new CSharpCodeProvider();
                var opts = new CompilerParameters {
                    OutputAssembly = Path.Combine(outputDirectoryPath, this.AssemblyName),
                    GenerateInMemory = false,
                    GenerateExecutable = true,
                    MainClass = "EpiSource.Unblocker.Hosting.Bootstrapper",
                    ReferencedAssemblies = { "System.dll", typeof(WorkerServerHost).Assembly.Location }
                };

                var result = provider.CompileAssemblyFromSource(opts, this.Source);
                if (result.NativeCompilerReturnValue == 0) return result.PathToAssembly;
                
                var ex = new InvalidOperationException("Failed to generate bootstrap assembly.");
                ex.Data["Errors"] = result.Errors;
                ex.Data["Output"] = result.Output;
                throw ex;
            }

            // bootstrapper must be installed into the same directory as the host assembly
            public static BootstrapAssemblySource CreateSideBySideBootstrapper() {
                var template = createSourceTemplate();
                var source = String.Format(template, "", "", "");
                var sourceHash = BobJenkinsOneAtATimeHash.CalculateHash(source);
                var assemblyName = String.Format("{0}-{1}.exe", formatUnblockerTitle(), typeof(WorkerServerHost).Assembly.GetName().Version, sourceHash);
                
                return new BootstrapAssemblySource(source, sourceHash, assemblyName);
            }

            // bootrapper that can be installed anywhere, but is bound to the location
            // of the host assembly at the time of source creation
            public static BootstrapAssemblySource CreateBoundBootstrapper() {
                var template = createSourceTemplate();
                
                var hostAssembly = typeof(WorkerServerHost).Assembly;
                var hostAssemblyResolver = @"

        static Bootstrapper() {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
                if (e.Name == @""" + hostAssembly.FullName + @""") {
                    return Assembly.LoadFile(@""" + hostAssembly.Location + @""");
                }

                return null;
            };
        }

";
                var sourceHash = new BobJenkinsOneAtATimeHash()
                                 .AppendString(template)
                                 .AppendString(hostAssemblyResolver)
                                 .GetHash();
                
                var source = String.Format(template, sourceHash, "+" + sourceHash);
                var assemblyName = String.Format("{0}-{1}+{2}.exe",
                    formatUnblockerTitle(), typeof(WorkerServerHost).Assembly.GetName().Version,
                    sourceHash);
                
                return new BootstrapAssemblySource(source, sourceHash, assemblyName);
            }

            private static string formatUnblockerTitle() {
                var hostAssembly = typeof(WorkerServerHost).Assembly;
                
                var unblockerTitle = "EpiSource.Unblocker.Bootstrap";
                if (hostAssembly.GetName().Name != "EpiSource.Unblocker") {
                    unblockerTitle += "@" + hostAssembly.GetName().Name;
                }
                return unblockerTitle;
            }

            private static string createSourceTemplate() {
                var hostAssembly = typeof(WorkerServerHost).Assembly;
                var hostClassName = typeof(WorkerServerHost).FullName;

                Expression<Action<string[]>> startMethod = args => WorkerServerHost.Start(args);
                var hostStartName = (startMethod.Body as MethodCallExpression).Method.Name;

                var unblockerVersion = hostAssembly.GetName().Version;
                var unblockerCopyright = hostAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>().Copyright;

                var unblockerTitle = formatUnblockerTitle();

                return @"
using System;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle(""" + unblockerTitle + @""")]
[assembly: AssemblyDescription(""Dynamically created worker process entrypoint for EpiSource.Unblocker."")]
[assembly: AssemblyConfiguration(""{1}"")]
[assembly: AssemblyCompany(""EpiSource"")]
[assembly: AssemblyProduct(""EpiSource.Unblocker"")]
[assembly: AssemblyCopyright(""" + unblockerCopyright + @""")]
[assembly: AssemblyTrademark("""")]
[assembly: AssemblyCulture("""")]
[assembly: ComVisible(false)]

[assembly: AssemblyVersion(""" + unblockerVersion + @""")]
[assembly: AssemblyFileVersion(""" + unblockerVersion + @""")]
[assembly: AssemblyInformationalVersion(""" + unblockerVersion + @"{2}"")]


namespace EpiSource.Unblocker.Hosting {{
    public static class Bootstrapper {{
{0}
        public static void Main(string[] args) {{
            " + hostClassName + "." + hostStartName + @"(args);
        }}
    }}
}}";
            }
        }
    }
}