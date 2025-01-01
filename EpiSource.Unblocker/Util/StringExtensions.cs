using System.Text.RegularExpressions;

namespace EpiSource.Unblocker.Util {
    public static class StringExtensions {
        public static string RegexReplace(this string input, string pattern, string replacement, RegexOptions options = RegexOptions.None) {
            return Regex.Replace(input, pattern, replacement, options);
        }
        
    }
}