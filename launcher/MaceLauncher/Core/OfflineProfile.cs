using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MaceLauncher.Core;

public static partial class OfflineProfile
{
    public static string Uuid(string nick)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + nick));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsValidNick(string nick) => NickPattern().IsMatch(nick);

    [GeneratedRegex(@"^[A-Za-z0-9_]{3,16}\z")]
    private static partial Regex NickPattern();
}
