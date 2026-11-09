using System.Security.Cryptography;

namespace XCloudSSD.Rclone;

/// <summary>
/// rclone's "obscure" encoding for password options (rclone/fs/config/obscure): AES-256-CTR with rclone's
/// published fixed key, random IV, base64url without padding. It is NOT encryption — rclone only requires
/// passwords in this form. XCloudSSD passes them to rclone through the process environment, never a file.
/// </summary>
public static class RcloneObscure
{
    private static readonly byte[] Key =
    [
        0x9c, 0x93, 0x5b, 0x48, 0x73, 0x0a, 0x55, 0x4d,
        0x6b, 0xfd, 0x7c, 0x63, 0xc8, 0x86, 0xa9, 0x2b,
        0xd3, 0x90, 0x19, 0x8e, 0xb8, 0x12, 0x8a, 0xfb,
        0xf4, 0xde, 0x16, 0x2b, 0x8b, 0x95, 0xf6, 0x38,
    ];

    /// <param name="deterministic">
    /// Same input → same output (IV derived from the value). rclone names its VFS cache folder after a hash of the
    /// remote's settings, so a value that changes on every start would orphan files waiting in the cache.
    /// </param>
    public static string Obscure(string plain, bool deterministic = false)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(plain);
        var iv = deterministic ? SHA256.HashData(data).AsSpan(0, 16).ToArray() : RandomNumberGenerator.GetBytes(16);
        var output = new byte[16 + data.Length];
        iv.CopyTo(output, 0);
        Ctr(iv, data, output.AsSpan(16));
        return Convert.ToBase64String(output).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Reveal(string obscured)
    {
        var s = obscured.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        var raw = Convert.FromBase64String(s);
        if (raw.Length < 16) throw new FormatException("Obscured value too short.");
        var plain = new byte[raw.Length - 16];
        Ctr(raw.AsSpan(0, 16).ToArray(), raw.AsSpan(16), plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }

    private static void Ctr(byte[] iv, ReadOnlySpan<byte> input, Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        var counter = (byte[])iv.Clone();
        var block = new byte[16];
        for (var i = 0; i < input.Length; i += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            var n = Math.Min(16, input.Length - i);
            for (var j = 0; j < n; j++) output[i + j] = (byte)(input[i + j] ^ block[j]);
            for (var k = 15; k >= 0 && ++counter[k] == 0; k--) { }
        }
    }
}
