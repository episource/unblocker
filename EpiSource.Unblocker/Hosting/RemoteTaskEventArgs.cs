using System;

using EpiSource.Unblocker.Util;

namespace EpiSource.Unblocker.Hosting {

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
    public sealed class TaskSucceededEventArgs : EventArgs {
        
        public TaskSucceededEventArgs(object result) {
            this.Result = result;
        }

        public object Result { get; private set; }

        public PortableEventArgs<TaskSucceededEventArgs> ToPortable() {
            return new PortableEventArgs<TaskSucceededEventArgs>(this);
        }
    }

    [Serializable]
    public sealed class TaskFailedEventArgs : EventArgs {
        public TaskFailedEventArgs(Exception e) {
            this.Exception = e;
        }

        public Exception Exception { get; private set; }
        
        public PortableEventArgs<TaskFailedEventArgs> ToPortable() {
            return new PortableEventArgs<TaskFailedEventArgs>(this);
        }
    }

    [Serializable]
    public sealed class TaskCanceledEventArgs : EventArgs {

        public TaskCanceledEventArgs(bool canceledVoluntarily) {
            this.CanceledVoluntarily = canceledVoluntarily;
        }
        
        public bool CanceledVoluntarily { get; private set; }
        
        public PortableEventArgs<TaskCanceledEventArgs> ToPortable() {
            return new PortableEventArgs<TaskCanceledEventArgs>(this);
        }
    }
}