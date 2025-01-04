using System;

namespace EpiSource.Unblocker.Util {

    public static class TypeReference {
        public static TypeReference<T> Of<T>() {
            return new TypeReference<T>();
        }
        
        public static TypeReference<T> Of<T>(T instance) {
            return new TypeReference<T>();
        }
    }
    public sealed class TypeReference<T> {
        internal TypeReference() {}
        
        public Type Type { get { return typeof(T); } }
    }
}