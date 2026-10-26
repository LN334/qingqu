using System.Security.Cryptography;
using System.Text;

namespace ZhifuWater.Core;

/// <summary>
/// RFC 7914 scrypt 的纯 BCL 实现（不引入 NuGet）。
/// Node 端用的是 <c>crypto.scryptSync(password, salt, 32)</c>，即 N=16384, r=8, p=1, dkLen=32。
/// 存储格式：<c>scrypt$&lt;salt&gt;$&lt;hex(hash)&gt;</c>，salt 是随机 16 字节的十六进制字符串（32 个 ASCII 字符），
/// 注意 Node 把 salt **字符串本身** 作为 scrypt 的 salt 字节（UTF-8），这里必须一致。
/// </summary>
public static class Scrypt
{
    public const int DefaultN = 16384;
    public const int DefaultR = 8;
    public const int DefaultP = 1;
    public const int DefaultDkLen = 32;

    /// <summary>scrypt(password, salt, N, r, p, dkLen)。</summary>
    public static byte[] DeriveBytes(string password, byte[] salt,
        int n = DefaultN, int r = DefaultR, int p = DefaultP, int dkLen = DefaultDkLen)
        => DeriveBytes(Encoding.UTF8.GetBytes(password), salt, n, r, p, dkLen);

    public static byte[] DeriveBytes(byte[] password, byte[] salt,
        int n = DefaultN, int r = DefaultR, int p = DefaultP, int dkLen = DefaultDkLen)
    {
        if (n <= 1 || (n & (n - 1)) != 0) throw new ArgumentException("N 必须是大于 1 的 2 的幂", nameof(n));
        if (r <= 0 || p <= 0 || dkLen <= 0) throw new ArgumentException("r/p/dkLen 必须为正数");

        int blockWords = 32 * r;                       // 128 * r bytes
        byte[] b = Pbkdf2(password, salt, 1, 128 * r * p);
        var xy = new uint[64 * r];                     // X || Y 工作区
        var v = new uint[blockWords * n];              // V 数组

        for (int i = 0; i < p; i++)
        {
            int off = i * 128 * r;
            Rombix(b, off, r, n, xy, v);
        }

        return Pbkdf2(password, b, 1, dkLen);
    }

    private static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int length)
    {
        using var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return kdf.GetBytes(length);
    }

    /// <summary>scryptROMix：作用在 b[off .. off+128r) 上的原地变换。</summary>
    private static void Rombix(byte[] b, int off, int r, int n, uint[] xy, uint[] v)
    {
        int words = 32 * r;
        var x = new uint[words];
        var y = new uint[words];
        BytesToWords(b, off, x);

        for (int i = 0; i < n; i++)
        {
            Array.Copy(x, 0, v, i * words, words);
            BlockMix(x, y, r);
            (x, y) = (y, x);
        }

        for (int i = 0; i < n; i++)
        {
            int j = (int)(x[(2 * r - 1) * 16] & (uint)(n - 1));
            for (int k = 0; k < words; k++) x[k] ^= v[j * words + k];
            BlockMix(x, y, r);
            (x, y) = (y, x);
        }

        WordsToBytes(x, b, off);
    }

    /// <summary>BlockMix：输入 x（32r words），输出 y（32r words）。</summary>
    private static void BlockMix(uint[] x, uint[] y, int r)
    {
        var tmp = new uint[16];
        Array.Copy(x, (2 * r - 1) * 16, tmp, 0, 16);

        for (int i = 0; i < 2 * r; i++)
        {
            for (int k = 0; k < 16; k++) tmp[k] ^= x[i * 16 + k];
            Salsa208(tmp);
            int dest = (i % 2 == 0) ? i / 2 : r + i / 2;
            Array.Copy(tmp, 0, y, dest * 16, 16);
        }
    }

    /// <summary>Salsa20/8 核心变换（原地，16 个 32 位字）。</summary>
    private static void Salsa208(uint[] b)
    {
        var x = new uint[16];
        Array.Copy(b, x, 16);

        for (int i = 0; i < 8; i += 2)
        {
            x[4] ^= Rotl(x[0] + x[12], 7); x[8] ^= Rotl(x[4] + x[0], 9);
            x[12] ^= Rotl(x[8] + x[4], 13); x[0] ^= Rotl(x[12] + x[8], 18);
            x[9] ^= Rotl(x[5] + x[1], 7); x[13] ^= Rotl(x[9] + x[5], 9);
            x[1] ^= Rotl(x[13] + x[9], 13); x[5] ^= Rotl(x[1] + x[13], 18);
            x[14] ^= Rotl(x[10] + x[6], 7); x[2] ^= Rotl(x[14] + x[10], 9);
            x[6] ^= Rotl(x[2] + x[14], 13); x[10] ^= Rotl(x[6] + x[2], 18);
            x[3] ^= Rotl(x[15] + x[11], 7); x[7] ^= Rotl(x[3] + x[15], 9);
            x[11] ^= Rotl(x[7] + x[3], 13); x[15] ^= Rotl(x[11] + x[7], 18);

            x[1] ^= Rotl(x[0] + x[3], 7); x[2] ^= Rotl(x[1] + x[0], 9);
            x[3] ^= Rotl(x[2] + x[1], 13); x[0] ^= Rotl(x[3] + x[2], 18);
            x[6] ^= Rotl(x[5] + x[4], 7); x[7] ^= Rotl(x[6] + x[5], 9);
            x[4] ^= Rotl(x[7] + x[6], 13); x[5] ^= Rotl(x[4] + x[7], 18);
            x[11] ^= Rotl(x[10] + x[9], 7); x[8] ^= Rotl(x[11] + x[10], 9);
            x[9] ^= Rotl(x[8] + x[11], 13); x[10] ^= Rotl(x[9] + x[8], 18);
            x[12] ^= Rotl(x[15] + x[14], 7); x[13] ^= Rotl(x[12] + x[15], 9);
            x[14] ^= Rotl(x[13] + x[12], 13); x[15] ^= Rotl(x[14] + x[13], 18);
        }

        for (int i = 0; i < 16; i++) b[i] += x[i];
    }

    private static uint Rotl(uint v, int c) => (v << c) | (v >> (32 - c));

    private static void BytesToWords(byte[] src, int off, uint[] dst)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            int p = off + i * 4;
            dst[i] = (uint)(src[p] | (src[p + 1] << 8) | (src[p + 2] << 16) | (src[p + 3] << 24));
        }
    }

    private static void WordsToBytes(uint[] src, byte[] dst, int off)
    {
        for (int i = 0; i < src.Length; i++)
        {
            int p = off + i * 4;
            uint w = src[i];
            dst[p] = (byte)w;
            dst[p + 1] = (byte)(w >> 8);
            dst[p + 2] = (byte)(w >> 16);
            dst[p + 3] = (byte)(w >> 24);
        }
    }

    /* ---------------- 口令存储格式（对齐 server/util.js） ---------------- */

    public static string RandomSaltHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>等价 JS <c>hashPassword(password)</c>，返回 <c>scrypt$salt$hex</c>。</summary>
    public static string HashPassword(string password, string? salt = null)
    {
        salt ??= RandomSaltHex();
        var hash = DeriveBytes(password, Encoding.UTF8.GetBytes(salt));
        return $"scrypt${salt}${Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>等价 JS <c>verifyPassword(password, stored)</c>，常时比较。</summary>
    public static bool VerifyPassword(string? password, string? stored)
    {
        try
        {
            if (stored is null) return false;
            var parts = stored.Split('$');
            if (parts.Length < 3 || parts[0] != "scrypt" || parts[1].Length == 0 || parts[2].Length == 0) return false;
            var expect = Convert.FromHexString(parts[2]);
            var test = DeriveBytes(password ?? "", Encoding.UTF8.GetBytes(parts[1]));
            return CryptographicOperations.FixedTimeEquals(expect, test);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>随机 token（base64url，默认 24 字节）——对齐 <c>crypto.randomBytes(n).toString('base64url')</c>。</summary>
    public static string Token(int bytes = 24) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Uid4() => Token(4);
}
