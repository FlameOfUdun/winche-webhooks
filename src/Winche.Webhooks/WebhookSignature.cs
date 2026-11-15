using System.Security.Cryptography;
using System.Text;

namespace Winche.Webhooks;

/// <summary>
/// How webhooks are signed: header <c>Winche-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of "t.body"&gt;</c>, with the
/// hook's signing secret. Receivers check it (and that t is recent) before trusting the body.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "Winche-Signature";

    public static string Sign(string secret, string body, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds();
        return $"t={t},v1={Hex(secret, $"{t}.{body}")}";
    }

    /// <summary>For receivers written in .NET: true when <paramref name="header"/> signs <paramref name="body"/> and is recent.</summary>
    public static bool Verify(string secret, string? header, string body, TimeSpan? tolerance = null, DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(header)) return false;
        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (!parts.TryGetValue("t", out var t) || !long.TryParse(t, out var seconds) || !parts.TryGetValue("v1", out var v1)) return false;
        var age = (now ?? DateTimeOffset.UtcNow) - DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (age.Duration() > (tolerance ?? TimeSpan.FromMinutes(5))) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hex(secret, $"{t}.{body}")), Encoding.ASCII.GetBytes(v1));
    }

    /// <summary>A new random signing secret.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hex(string secret, string text) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text)));
}
