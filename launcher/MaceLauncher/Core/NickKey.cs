using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MaceLauncher.Core;

public sealed class NickKey(string nick, string privateKey, string publicKey)
{
    public const int ShortestPassword = 6;
    public const string PrivateVariable = "MACE_KEY";
    public const string PublicVariable = "MACE_PUBLIC";

    private const string Purpose = "M.A.C.E nick key v1:";
    private const int Rounds = 600_000;
    private const int ScalarBytes = 32;
    private static readonly BigInteger CurveOrder = BigInteger.Parse(
        "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.HexNumber);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("M.A.C.E nick key");

    public string Nick { get; } = nick;
    public string PrivateKey { get; } = privateKey;
    public string PublicKey { get; } = publicKey;

    public static bool IsValidPassword(string password) => password.Length >= ShortestPassword;

    public bool IsFor(string other) => string.Equals(Nick, other, StringComparison.OrdinalIgnoreCase);

    public static NickKey Derive(string nick, string password)
    {
        var salt = Encoding.UTF8.GetBytes(Purpose + nick.ToLowerInvariant());
        var seed = Rfc2898DeriveBytes.Pbkdf2(password, salt, Rounds, HashAlgorithmName.SHA256, ScalarBytes);
        var scalar = new BigInteger(seed, isUnsigned: true, isBigEndian: true) % (CurveOrder - 1) + 1;
        var bytes = scalar.ToByteArray(isUnsigned: true, isBigEndian: true);
        var d = new byte[ScalarBytes];
        bytes.CopyTo(d, ScalarBytes - bytes.Length);
        using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
        return new(nick, Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static NickKey? Load(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
            var key = JsonSerializer.Deserialize<NickKey>(json);
            return key is { Nick: not null, PrivateKey: not null, PublicKey: not null } ? key : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Info("сохранённый ключ ника не читается, пароль нужно ввести заново: " + e.Message);
            return null;
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file,
            ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), Entropy, DataProtectionScope.CurrentUser));
    }
}
