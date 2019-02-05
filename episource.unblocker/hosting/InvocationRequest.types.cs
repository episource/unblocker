using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;

namespace episource.unblocker.hosting {
    public partial class InvocationRequest {
        [Serializable]
        public sealed class PortableInvocationRequest {
            // Actual invocation request stored as binary data to prevent remoting framework from automatically
            // deserializing it: This required loading all referenced assemblies in the AppDomain running the server
            // application. However, assemblies should be loaded in a task specific  AppDomain.
            private readonly byte[] serializedInvocationRequest;

            private readonly AssemblyReferencePool referencePool;
            private readonly string methodName;
            private readonly string applicationBase;

            public PortableInvocationRequest(InvocationRequest request) {
                this.serializedInvocationRequest = Serialize(request);
                this.referencePool = new AssemblyReferencePool(AppDomain.CurrentDomain);
                this.methodName = request.Method.DeclaringType.FullName + "." + request.Method.Name;
                this.applicationBase = AppDomain.CurrentDomain.BaseDirectory;
            }

            public string MethodName {
                get { return this.methodName; }
            }

            public string ApplicationBase {
                get { return this.applicationBase; }
            }

            public InvocationRequest ToInvocationRequest() {
                this.referencePool.AttachToDomain(AppDomain.CurrentDomain);
                try {
                    return Deserialize(this.serializedInvocationRequest);
                } finally {
                    this.referencePool.DetachFromDomain(AppDomain.CurrentDomain);
                }
            }

            private static byte[] Serialize(InvocationRequest request) {
                var binFormatter = new BinaryFormatter();
                var bufferStream = new MemoryStream();
                binFormatter.Serialize(bufferStream, request);
                return bufferStream.ToArray();
            }

            private static InvocationRequest Deserialize(byte[] serializedRequest) {
                var binFormatter = new BinaryFormatter();
                var serializedStream = new MemoryStream(serializedRequest);
                return (InvocationRequest) binFormatter.Deserialize(serializedStream);
            }
        }

        [Serializable]
        private sealed class AssemblyReferencePool {
            private readonly IDictionary<string, string> nameToLocationMap;

            public AssemblyReferencePool(AppDomain hostDomain) {
                hostDomain.GetAssemblies().Where(a => !a.IsDynamic).ToDictionary(a => a.FullName, a => a.Location);
            }

            public void AttachToDomain(AppDomain target) {
                target.AssemblyResolve += this.ResolveAssembly;
                target.ReflectionOnlyAssemblyResolve += this.ResolveAssemblyReflectionOnly;
            }

            public void DetachFromDomain(AppDomain target) {
                target.AssemblyResolve -= this.ResolveAssembly;
                target.ReflectionOnlyAssemblyResolve -= this.ResolveAssemblyReflectionOnly;
            }

            public string GetAssemblyLocation(string fullName) {
                return this.nameToLocationMap.ContainsKey(fullName) ? this.nameToLocationMap[fullName] : null;
            }

            private Assembly ResolveAssembly(object sender, ResolveEventArgs args) {
                var location = this.GetAssemblyLocation(args.Name);
                return location != null ? Assembly.LoadFile(location) : null;
            }

            private Assembly ResolveAssemblyReflectionOnly(object sender, ResolveEventArgs args) {
                var location = this.GetAssemblyLocation(args.Name);
                return location != null ? Assembly.ReflectionOnlyLoadFrom(location) : null;
            }
        }

        [Serializable]
        private struct CancellationTokenMarker { }
    }
}