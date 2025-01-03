using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

namespace EpiSource.Unblocker.Util {
    public static class SerializationExtensions {
        public static byte[] SerializeToBinary(this object obj) {
            var binFormatter = new BinaryFormatter();
            var bufferStream = new MemoryStream();
            binFormatter.Serialize(bufferStream, obj);
            return bufferStream.ToArray();
        }

        public static T DeserializeFromBinary<T>(this byte[] bin) {
            var binFormatter = new BinaryFormatter();
            var serializedStream = new MemoryStream(bin);
            return (T) binFormatter.Deserialize(serializedStream);
        }
    }
}