using System;
using System.Security.Permissions;
using System.Security.Policy;

namespace EpiSource.Unblocker.Hosting {
    public static class HostingExtensions {
        public static StrongName GetStrongNameOfAssembly(this Type t) {
            var assemblyName = t.Assembly.GetName();
            var pubKeyBytes = assemblyName.GetPublicKey();
            if (pubKeyBytes == null || pubKeyBytes.Length == 0) {
                return null;
            }

            return new StrongName(
                new StrongNamePublicKeyBlob(pubKeyBytes), assemblyName.Name, assemblyName.Version);
        }

        public static StrongName[] GetStrongNameOfAssemblyAsArray(this Type t) {
            var sn = t.GetStrongNameOfAssembly();
            return sn != null ? new[] {sn} : new StrongName[] { };
        }
        
        public static void InvokeEvent<T>(this T eventHandler, Action<T> handlerInvocation) {
            if (eventHandler != null) {
                handlerInvocation(eventHandler);
            }
        }
    }
}