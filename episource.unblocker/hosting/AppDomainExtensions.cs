using System;
using System.Security.Permissions;
using System.Security.Policy;

namespace episource.unblocker.hosting {
    public static class AppDomainExtensions {
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
    }
}