using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace EpiSource.Unblocker.Hosting {
    public sealed partial class PortableInvocationRequest {
        [Serializable]
        private sealed class AssemblyReferencePool {
            private readonly IDictionary<string, string> nameToLocationMap;

            public AssemblyReferencePool(AppDomain hostDomain) {
                // note: it's possible for two assemblies with same name to be loaded (different location!)
                // -> choose first
                this.nameToLocationMap = hostDomain.GetAssemblies()
                                                   .Where(a => !a.IsDynamic && File.Exists(a.Location))
                                                   .GroupBy(a => a.FullName)
                                                   .Select(g => g.First())
                                                   .ToDictionary(a => a.FullName, a => a.Location);
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
    }
}