using FastDrop.Infrastructure.Security;
using Xunit;

namespace FastDrop.Tests.Security;

public class TokenGeneratorTests
{
    private readonly TokenGenerator _sut = new(); // sut = System Under Test

    [Fact]
    public void GenerateToken_ReturnsDistinctRawTokens()
    {
        var token1 = _sut.GenerateToken();
        var token2 = _sut.GenerateToken();

        Assert.NotEqual(token1.RawToken, token2.RawToken);
        Assert.NotEmpty(token1.RawToken);
    }

    [Fact]
    public void VerifyToken_ReturnsTrueForValidToken()
    {
        var tokenData = _sut.GenerateToken();

        var isValid = _sut.VerifyToken(tokenData.RawToken, tokenData.HashedToken);

        Assert.True(isValid);
    }

    [Fact]
    public void VerifyToken_ReturnsFalseForInvalidToken()
    {
        var tokenData = _sut.GenerateToken();

        var isValid = _sut.VerifyToken("completely_wrong_token", tokenData.HashedToken);

        Assert.False(isValid);
    }
}
