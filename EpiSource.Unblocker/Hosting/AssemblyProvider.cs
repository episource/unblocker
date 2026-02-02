using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using EpiSource.Unblocker.Util;

using Microsoft.CSharp;

namespace EpiSource.Unblocker.Hosting {
    
    public abstract class AssemblyProvider {

        protected sealed class AssemblySource {
            
            public readonly string SourceHash;
            public readonly string Source;
            public readonly bool IsExecutable;
            public readonly string MainClass;
            public readonly string AssemblyName;
            public readonly IReadOnlyList<string> Dependencies;

            public AssemblySource(string source, string assemblyBaseName, IReadOnlyList<string> dependencies, string mainClassIfExecutable = null) {
                var sourceHash = BobJenkinsOneAtATimeHash.CalculateHash(source);
                this.SourceHash = String.Format("0x{0:x8}", sourceHash);

                this.Source = source.Replace("{hash}", this.SourceHash);

                this.MainClass = mainClassIfExecutable;
                this.IsExecutable = mainClassIfExecutable != null;
                this.AssemblyName = assemblyBaseName + "-" + this.SourceHash + (this.IsExecutable ? ".exe" : ".dll");
                this.Dependencies = new [] {
                    "System.dll"
                }.Concat(dependencies).ToList().AsReadOnly();
            }

            public string Compile(string outputDirectoryPath) {
                var provider = new CSharpCodeProvider();
                
                var opts = new CompilerParameters {
                    OutputAssembly = Path.Combine(outputDirectoryPath, this.AssemblyName),
                    GenerateInMemory = false,
                    GenerateExecutable = this.IsExecutable,
                };
                foreach (var dependency in this.Dependencies) {
                    opts.ReferencedAssemblies.Add(dependency);
                }
                if (this.MainClass != null) {
                    opts.MainClass = this.MainClass;
                }
                

                var result = provider.CompileAssemblyFromSource(opts, this.Source);
                if (result.NativeCompilerReturnValue == 0) return result.PathToAssembly;

                var messageBuilder = new StringBuilder();
                messageBuilder.Append("Failed to generate bootstrap assembly");

                if (result.Errors.Count > 0) {
                    messageBuilder.AppendLine(":").AppendLine("");

                    var sourceLines = this.Source.Replace("\n\r", "\n").Replace("\r\n", "\n").Split('\n');
                    foreach (CompilerError err in result.Errors) {
                        messageBuilder.Append(" - ").Append(err.ErrorNumber).Append(": ").AppendLine(err.ErrorText);
                        if (err.Line > 0 && sourceLines.Length >= err.Line) {
                            messageBuilder.Append("   @L").Append(err.Line).Append(": ").AppendLine(sourceLines[err.Line - 1].Trim());
                        }
                    }
                    messageBuilder.AppendLine("");
                } else {
                    messageBuilder.Append(".");
                }

                var outputBuilder = new StringBuilder();
                foreach (var line in result.Output) {
                    outputBuilder.AppendLine(line);
                }

                var ex = new InvalidOperationException(messageBuilder.ToString());
                ex.Data["Output"] = outputBuilder.ToString();
                ex.Data["Source"] = this.Source;
                throw ex;
            }
        }

        private readonly SemaphoreSlim semaphoreOneAtATime = new SemaphoreSlim(1, 1);
        protected readonly string dynamicAssemblyLocation;
        protected readonly bool noSideBySide;

        protected AssemblyProvider(string dynamicAssemblyLocation, bool noSideBySide) {
            this.dynamicAssemblyLocation = dynamicAssemblyLocation ?? Path.GetTempPath();
            this.noSideBySide = noSideBySide;
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

        protected abstract string EnsureAvailableInternal();
    }
}