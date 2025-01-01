using System.IO;

namespace EpiSource.Unblocker.Util {
    public static class StreamExtensions {
        public static string ReadAllTextAndClose(this Stream stream) {
            if (stream == null) {
                return null;
            }
            
            try {
                using (var sr = new StreamReader(stream)) {
                    return sr.ReadToEnd();
                }
            } finally {
                stream.Close();
            }
        }
    }
}