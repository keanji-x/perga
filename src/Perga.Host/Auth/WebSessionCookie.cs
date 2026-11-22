using System.Buffers.Text;
using System.Security.Cryptography;
using Perga.Host.Configuration;

namespace Perga.Host.Auth;

internal static class WebSessionCookie
{
    public const string SessionIdHeader = "X-Perga-Session-Id";

    public static (string Token, string Hash) Create()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        try
        {
            return (Base64Url.EncodeToString(raw), Convert.ToHexStringLower(SHA256.HashData(raw)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public static string? ReadHash(HttpContext context, PergaHostOptions options)
    {
        if (!context.Request.Cookies.TryGetValue(Name(context, options), out var cookie) ||
            cookie.Length != 43 || cookie.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            return null;

        Span<byte> raw = stackalloc byte[32];
        try
        {
            return Base64Url.TryDecodeFromChars(cookie, raw, out var bytes) && bytes == raw.Length &&
                   Base64Url.EncodeToString(raw) == cookie
                ? Convert.ToHexStringLower(SHA256.HashData(raw))
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public static string Name(HttpContext context, PergaHostOptions options) =>
        IsSecure(context, options) ? "__Host-perga_session" : "perga_session";

    public static CookieOptions Attributes(HttpContext context, PergaHostOptions options) =>
        new() { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", Secure = IsSecure(context, options) };

    private static bool IsSecure(HttpContext context, PergaHostOptions options) =>
        context.Request.IsHttps || options.AuthCookieSecurePolicy == AuthCookieSecurePolicy.Always;
}
