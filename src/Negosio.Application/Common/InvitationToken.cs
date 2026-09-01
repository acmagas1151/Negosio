using System.Security.Cryptography;

namespace Negosio.Application.Common;

/// <summary>
/// Single-use staff-invitation tokens. The raw token (returned once, put in the accept URL) is a
/// 256-bit CSPRNG value; only its SHA-256 hash is persisted, so a database leak does not yield a
/// usable token.
/// </summary>
public static class InvitationToken
{
    public sealed record Pair(string Raw, string Hash);

    public static Pair Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Base64Url(bytes);
        return new Pair(raw, Hash(raw));
    }

    public static string Hash(string raw)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
