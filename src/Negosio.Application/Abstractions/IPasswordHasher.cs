namespace Negosio.Application.Abstractions;

/// <summary>Wraps a reputable password hashing implementation (no custom cryptography).</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string passwordHash);
}
