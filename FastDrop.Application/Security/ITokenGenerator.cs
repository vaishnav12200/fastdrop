namespace FastDrop.Application.Security;

// A record is a concise way to create an immutable data object.
public record TokenData(string RawToken, string HashedToken);

public interface ITokenGenerator
{
    // Generates a cryptographically secure random token and its hash
    TokenData GenerateToken(int length = 32);
    
    // Verifies if a raw token provided by a user matches the stored hash
    bool VerifyToken(string rawToken, string hashedToken);
}
