using System;
using System.Runtime.CompilerServices;

using EpiSource.Unblocker.Util;

namespace EpiSource.Unblocker.Hosting {

    [Serializable]
    public class RemoteTaskEventArgs : EventArgs {

        private object target;
        public RemoteTaskEventArgs(object target) {
            this.target = target;
        }

        public object Target {
            get {
                return this.target;
            }
        }

        public T GetTargetAs<T>() {
            return (T)this.target;
        }
    }

    [Serializable]
    public sealed class PortableEventArgs<T> : EventArgs {
        private readonly byte[] serializedEventArgs;

        internal PortableEventArgs(T eventArgs) {
            this.serializedEventArgs = eventArgs.SerializeToBinary();
        }

        public T Deserialize() {
            return this.serializedEventArgs.DeserializeFromBinary<T>();
        }
    }
    
    [Serializable]
    public sealed class TaskSucceededEventArgs : RemoteTaskEventArgs {
        
        public TaskSucceededEventArgs(object target, object result, bool hasResult) : base(target) {
            this.Result = result;
            this.HasResult = hasResult;
        }

        public object Result { get; private set; }
        
        public bool HasResult { get; private set; }

        public PortableEventArgs<TaskSucceededEventArgs> ToPortable() {
            return new PortableEventArgs<TaskSucceededEventArgs>(this);
        }
    }

    [Serializable]
    public sealed class TaskFailedEventArgs : RemoteTaskEventArgs {
        public TaskFailedEventArgs(object target, Exception e) : base(target) {
            this.Exception = e;
        }

        public Exception Exception { get; private set; }
        
        public PortableEventArgs<TaskFailedEventArgs> ToPortable() {
            return new PortableEventArgs<TaskFailedEventArgs>(this);
        }
    }

    [Serializable]
    public sealed class TaskCanceledEventArgs : RemoteTaskEventArgs {

        public TaskCanceledEventArgs(object target, bool canceledVoluntarily) : base(target) {
            this.CanceledVoluntarily = canceledVoluntarily;
        }
        
        public bool CanceledVoluntarily { get; private set; }
        
        public PortableEventArgs<TaskCanceledEventArgs> ToPortable() {
            return new PortableEventArgs<TaskCanceledEventArgs>(this);
        }
    }
}