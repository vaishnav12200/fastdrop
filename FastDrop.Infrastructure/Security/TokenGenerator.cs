using System.Security.Cryptography;
using System.Text;
using FastDrop.Application.Security;

namespace FastDrop.Infrastructure.Security;

public class TokenGenerator : ITokenGenerator
{
    public TokenData GenerateToken(int length = 32)
    {
        // 1. Generate cryptographically secure random bytes
        var randomBytes = RandomNumberGenerator.GetBytes(length);

        // 2. Convert to a URL-safe Base64 string (remove padding, replace + with -, / with _)
        var rawToken = Convert.ToBase64String(randomBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        // 3. Hash it for storage
        var hashedToken = HashToken(rawToken);

        return new TokenData(rawToken, hashedToken);
    }

    public bool VerifyToken(string rawToken, string hashedToken)
    {
        var computedHash = HashToken(rawToken);
        
        // Cryptographic constant-time comparison prevents timing attacks
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHash),
            Encoding.UTF8.GetBytes(hashedToken)
        );
    }

    private string HashToken(string rawToken)
    {
        // SHA-256 is perfectly fine here since these are long, cryptographically random, 
        // single-use tokens, NOT human passwords. We don't need expensive hashing like bcrypt.
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
