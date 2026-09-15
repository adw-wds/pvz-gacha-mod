using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PvzShared
{
    /// <summary>
    /// 与游戏 GameStart.CalculateHash 逐字节一致的校验：
    /// hex( MD5( MD5( MD5( fileBytes ) ) ) )，小写。
    /// 游戏侧比对时对 .md5 内容做了 Trim()，所以这里写出的内容不带换行。
    /// </summary>
    public static class SaveHash
    {
        public static string ComputeBytes(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using (HashAlgorithm md5 = HashAlgorithm.Create("MD5"))
            {
                // 复用同一实例连算三次：与游戏写法一致
                byte[] d = md5.ComputeHash(bytes);
                d = md5.ComputeHash(d);
                d = md5.ComputeHash(d);
                var sb = new StringBuilder(d.Length * 2);
                for (int i = 0; i < d.Length; i++) sb.Append(d[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string ComputeFile(string path)
        {
            return ComputeBytes(File.ReadAllBytes(path));
        }

        public static bool Verify(string saveFile, string hashFile)
        {
            if (!File.Exists(saveFile) || !File.Exists(hashFile)) return false;
            return string.Equals(ComputeFile(saveFile), File.ReadAllText(hashFile).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        public static void WriteHash(string saveFile, string hashFile)
        {
            // 只用两参重载：游戏 mscorlib 里 WriteAllText 的三参版不存在
            File.WriteAllText(hashFile, ComputeFile(saveFile));
        }
    }
}
