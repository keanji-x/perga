using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Perga.Kernel.Ports;

namespace Perga.Host.Auth;

public interface IAuthPasswordHasher
{
    string Hash(string password);
    PasswordVerificationResult Verify(string password, string encodedHash);
    PasswordVerificationResult VerifyDummy(string password);
}

public sealed class AuthPasswordHasher : IAuthPasswordHasher
{
    public const int DefaultIterationCount = 220_000;
    private static readonly AuthUserRecord HashContext = new("", "", "", "owner", "", 1, 1, null);
    private readonly PasswordHasher<AuthUserRecord> _hasher;

    public AuthPasswordHasher(IOptions<PasswordHasherOptions> options)
    {
        _hasher = new PasswordHasher<AuthUserRecord>(options);
        DummyHash = Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public string DummyHash { get; }

    public string Hash(string password) => _hasher.HashPassword(HashContext, password);

    public PasswordVerificationResult Verify(string password, string encodedHash) =>
        _hasher.VerifyHashedPassword(HashContext, encodedHash, password);

    public PasswordVerificationResult VerifyDummy(string password) => Verify(password, DummyHash);

    public static string HashPassword(string password, int iterationCount = DefaultIterationCount) =>
        new PasswordHasher<AuthUserRecord>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterationCount
        })).HashPassword(HashContext, password);
}
