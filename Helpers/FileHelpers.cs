using System.Security.Cryptography;
using System.Text;

namespace BookHeaven.Core.Helpers;

public static class FileHelpers
{

    /*
     * Computes a partial MD5 hash of a file by sampling its content at specific offsets.
     * This method has been adapted from KOReader to generate the same hash
     */
    public static async Task<string?> GetPartialMd5HashAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        if (!File.Exists(filePath)) return null;

        const int step = 1024;
        const int size = 1024;

        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var length = fs.Length;

            using var md5 = MD5.Create();
            var anySample = false;

            // First sample: offset 0 (head of file)
            if (length > 0)
            {
                fs.Seek(0, SeekOrigin.Begin);
                var toRead0 = (int)Math.Min(size, length);
                if (toRead0 > 0)
                {
                    var buffer0 = new byte[toRead0];
                    var read0 = 0;
                    while (read0 < toRead0)
                    {
                        var r = await fs.ReadAsync(buffer0.AsMemory(read0, toRead0 - read0)).ConfigureAwait(false);
                        if (r == 0) break;
                        read0 += r;
                    }
                    if (read0 > 0)
                    {
                        md5.TransformBlock(buffer0, 0, read0, null, 0);
                        anySample = true;
                    }
                }
            }

            // Then samples at offsets step << (2*i) for i = 0..10
            for (var i = 0; i <= 10; i++)
            {
                var offset = ((long)step) << (2 * i);
                if (offset >= length) break;

                fs.Seek(offset, SeekOrigin.Begin);

                var toRead = (int)Math.Min(size, length - offset);
                if (toRead <= 0) break;

                var buffer = new byte[toRead];
                var read = 0;
                while (read < toRead)
                {
                    var r = await fs.ReadAsync(buffer.AsMemory(read, toRead - read)).ConfigureAwait(false);
                    if (r == 0) break;
                    read += r;
                }

                if (read > 0)
                {
                    md5.TransformBlock(buffer, 0, read, null, 0);
                    anySample = true;
                }
                else
                {
                    break;
                }
            }

            if (!anySample) return null;

            md5.TransformFinalBlock([], 0, 0);
            var hash = md5.Hash;
            if (hash == null || hash.Length == 0) return null;
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }
}