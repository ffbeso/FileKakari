using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FileKakari;

public static class BundledThemeUpgrade
{
    public static bool TryApply(string sourcePath, string userPath, string previousHash)
    {
        if (!File.Exists(sourcePath) || !File.Exists(userPath)) return false;
        var text = File.ReadAllText(userPath).Replace("\r\n", "\n");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (!string.Equals(hash, previousHash, StringComparison.OrdinalIgnoreCase)) return false;
        File.Copy(sourcePath, userPath, overwrite: true);
        return true;
    }
}
