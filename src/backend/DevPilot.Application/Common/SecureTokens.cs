using System.Security.Cryptography;
using System.Text;

namespace DevPilot.Application.Common;

public static class SecureTokens
{
    /// <summary>Cryptographically random URL-safe token (256 bits by default).</summary>
    public static string Generate(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 hex digest. High-entropy tokens do not need a slow hash.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool FixedTimeEqualsHex(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}

public static class Slug
{
    // Explicit transliteration: the app runs with InvariantGlobalization, where Unicode normalization is unavailable.
    private const string Accented = "áàäâãåéèëêíìïîóòöôõúùüûñçý";
    private const string Plain = "aaaaaaeeeeiiiiooooouuuuncy";

    public static string From(string text, int maxLength = 48)
    {
        var sb = new StringBuilder(text.Length);
        var lastDash = true;
        foreach (var raw in text.ToLowerInvariant())
        {
            var index = Accented.IndexOf(raw, StringComparison.Ordinal);
            var ch = index >= 0 ? Plain[index] : raw;
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && (char.IsWhiteSpace(ch) || ch is '-' or '_' or '.'))
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength].Trim('-');
        }

        return slug.Length == 0 ? "org" : slug;
    }
}
