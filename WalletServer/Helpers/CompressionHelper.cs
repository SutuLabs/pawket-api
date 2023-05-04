using Ionic.Zlib;

namespace WalletServer.Helpers;

public static class CompressionHelper
{
    public static byte[] CompressZlib(this byte[] input)
    {
        if (input.Length == 0) return input;

        var output = ZlibStream.CompressBuffer(input);
        return output;
    }

    public static byte[] CompressGzip(this byte[] input)
    {
        using (var result = new MemoryStream())
        {
            using (var compressionStream = new GZipStream(result, CompressionMode.Compress))
            {
                compressionStream.Write(input, 0, input.Length);
                compressionStream.Flush();
            }
            return result.ToArray();
        }
    }
}