using System.Text;

namespace EpiSource.Unblocker.Util {
    /**
     * Bob Jenkins' non-cryptographic One-at-a-time hash function.
     *
     * See: https://www.burtleburtle.net/bob/hash/doobs.html
     */
    public sealed class BobJenkinsOneAtATimeHash {
        private uint preAvalancheHash = 0;

        public BobJenkinsOneAtATimeHash AppendString(string s) {
            return this.AppendBytes(UnicodeEncoding.Unicode.GetBytes(s));
        }

        public BobJenkinsOneAtATimeHash AppendBytes(byte[] bytes) {
            foreach (byte b in bytes) {
                unchecked {
                    this.preAvalancheHash += b;
                    this.preAvalancheHash += (this.preAvalancheHash << 10);
                    this.preAvalancheHash ^= (this.preAvalancheHash >> 6);
                }
            }
            return this;
        }

        public uint GetHash() {
            unchecked {
                uint hash = this.preAvalancheHash;
                hash += (hash << 3);
                hash ^= (hash >> 11);
                hash += (hash << 15);
                return hash;
            }
        }

        public static uint CalculateHash(string s) {
            return new BobJenkinsOneAtATimeHash().AppendString(s).GetHash();
        }
    }
}