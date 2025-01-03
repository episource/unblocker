using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using EpiSource.Unblocker.Util;

using Microsoft.CSharp;

namespace EpiSource.Unblocker.Hosting {
    public sealed class BootstrapAssemblyProvider {
        
        public static readonly BootstrapAssemblyProvider Instance = new BootstrapAssemblyProvider();
        
        private readonly SemaphoreSlim semaphoreOneAtATime = new SemaphoreSlim(1, 1);
        private string assemblyPath = null;

        private BootstrapAssemblyProvider() {
            
        }
        
        public async Task<string> EnsureAvailableAsync() {
            await this.semaphoreOneAtATime.WaitAsync();
            try {
                if (this.assemblyPath == null || !File.Exists(this.assemblyPath)) {
                    this.assemblyPath = await Task.Run(() => CreateBootstrapAssembly());
                }

                return this.assemblyPath;
            } finally {
                this.semaphoreOneAtATime.Release();
            }
        }

        public string EnsureAvailable() {
            this.semaphoreOneAtATime.Wait();
            try {
                if (this.assemblyPath == null || !File.Exists(this.assemblyPath)) {
                    this.assemblyPath = CreateBootstrapAssembly();
                }

                return this.assemblyPath;
            } finally {
                this.semaphoreOneAtATime.Release();
            }
        }
        
        private static string CreateBootstrapAssembly() {
            var hostAssembly = typeof(WorkerServerHost).Assembly;
            var hostClassName = typeof(WorkerServerHost).FullName;

            Expression<Action<string[]>> startMethod = args => WorkerServerHost.Start(args);
            var hostStartName = (startMethod.Body as MethodCallExpression).Method.Name;

            Version unblockerVersion;
            string unblockerCopyright;
            var assemblyInfoSource = hostAssembly.GetManifestResourceStream("EpiSource.Unblocker.Properties.AssemblyInfo.cs").ReadAllTextAndClose();
            if (assemblyInfoSource != null) {
                unblockerVersion = Version.Parse(Regex.Match(assemblyInfoSource, @"^\[assembly: AssemblyVersion\(""([^""]+)", RegexOptions.Multiline).Groups[1].Value);
                unblockerCopyright = Regex.Match(assemblyInfoSource, @"^\[assembly: AssemblyCopyright\(""([^""]+)", RegexOptions.Multiline).Groups[1].Value;
            } else {
                unblockerVersion = hostAssembly.GetName().Version;
                unblockerCopyright = hostAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>().Copyright;
            }
            
            var source = @"
using System;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle(""EpiSource.Unblocker.Bootstrap"")]
[assembly: AssemblyDescription(""Dynamically crated worker process entrypoint for EpiSource.Unblocker."")]
[assembly: AssemblyConfiguration("""")] // placeholder for bootstrapper variant hash
[assembly: AssemblyCompany(""EpiSource"")]
[assembly: AssemblyProduct(""EpiSource.Unblocker"")]
[assembly: AssemblyCopyright(""" + unblockerCopyright + @""")]
[assembly: AssemblyTrademark("""")]
[assembly: AssemblyCulture("""")]
[assembly: ComVisible(false)]

[assembly: AssemblyVersion(""" + unblockerVersion + @""")]
[assembly: AssemblyFileVersion(""" + unblockerVersion + @""")]
[assembly: AssemblyInformationalVersion(""" + unblockerVersion + @""")]


namespace EpiSource.Unblocker.Hosting {
    public static class Bootstrapper {

        static Bootstrapper() {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
                if (e.Name == @""" + hostAssembly.FullName + @""") {
                    return Assembly.LoadFile(@""" + hostAssembly.Location + @""");
                }

                return null;
            };
        }

        public static void Main(string[] args) {
            " + hostClassName + "." + hostStartName + @"(args);
        }
    }
}
             ";

            
            // encode variant hash in bootstrapper assembly attributes
            var hashString = String.Format("0x{0:x8}", BobJenkinsOneAtATimeHash.CalculateHash(source));
            source = source.Replace("[assembly: AssemblyConfiguration(\"\")]", "[assembly: AssemblyConfiguration(\"0x" + hashString + "\")]")
                           .RegexReplace(@"(?<=\[assembly: AssemblyInformationalVersion\(""[^""]*)(?="")", "+" + hashString);
            
            var provider = new CSharpCodeProvider();
            var opts = new CompilerParameters {
                OutputAssembly = Path.Combine(Path.GetTempPath(), String.Format("EpiSource.Unblocker.Bootstrap_{0}+{1}.exe", unblockerVersion, hashString)),
                GenerateInMemory = false,
                GenerateExecutable = true,
                MainClass = "EpiSource.Unblocker.Hosting.Bootstrapper",
                ReferencedAssemblies = { "System.dll", hostAssembly.Location }
            };

            var result = provider.CompileAssemblyFromSource(opts, source);
            if (result.NativeCompilerReturnValue == 0) return result.PathToAssembly;
            
            var ex = new InvalidOperationException("Failed to generate bootstrap assembly.");
            ex.Data["Errors"] = result.Errors;
            ex.Data["Output"] = result.Output;
            throw ex;

        }
    }
}