using System;

using EpiSource.Unblocker.Util;

namespace EpiSource.Unblocker.Hosting {
    
    public interface IPortableInvocationRequest {
        string MethodName { get; }
        string ApplicationBase { get; }

        IInvocationRequest ToInvocationRequest();
    }
    
    // Note: Without generic type parameters! This class is deserialized by the worker in a context
    // where the AssemblyReferencePool cannot be applied. Therefor, only types from the unblocker assembly
    // are available. Generic type parameters could reference foreign types. This would cause type
    // load exceptions during worker side deserialization.
    [Serializable]
    public sealed partial class PortableInvocationRequest : IPortableInvocationRequest {
        // Actual invocation request stored as binary data to prevent remoting framework from automatically
        // deserializing it: This required loading all referenced assemblies in the AppDomain running the server
        // application. However, assemblies should be loaded in a task specific AppDomain.
        private readonly byte[] serializedInvocationRequest;

        private readonly AssemblyReferencePool referencePool;
        private readonly string methodName;
        private readonly string applicationBase;

        public PortableInvocationRequest(IInvocationRequest request) {
            if (request == null) {
                throw new ArgumentNullException("request");
            }
            
            this.serializedInvocationRequest = request.SerializeToBinary();
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

        public IInvocationRequest ToInvocationRequest() {
            this.referencePool.AttachToDomain(AppDomain.CurrentDomain);
            try {
                return this.serializedInvocationRequest.DeserializeFromBinary<IInvocationRequest>();
            } finally {
                this.referencePool.DetachFromDomain(AppDomain.CurrentDomain);
            }
        }
    }
}