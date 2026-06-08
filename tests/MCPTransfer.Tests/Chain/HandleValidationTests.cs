using MCPTransfer.Core.Chain;

namespace MCPTransfer.Tests.Chain;

public class HandleValidationTests
{
    [Theory]
    [InlineData("alice-ai")]
    [InlineData("abc")]            // min length
    [InlineData("gpt5-instance-42")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123ab")] // 32 chars, max length
    public void Validate_AcceptsValidHandles(string handle)
        => HandleValidation.Validate(handle);

    [Theory]
    [InlineData("ab")]             // too short
    [InlineData("abcdefghijklmnopqrstuvwxyz0123abc")] // 33 chars
    [InlineData("Alice")]          // uppercase
    [InlineData("alice.ai")]       // dot
    [InlineData("alice ai")]       // space
    [InlineData("alice_ai")]       // underscore
    [InlineData("-alice")]         // leading hyphen
    [InlineData("alice-")]         // trailing hyphen
    public void Validate_RejectsInvalidHandles(string handle)
    {
        Assert.Throws<ArgumentException>(() => HandleValidation.Validate(handle));
        Assert.False(HandleValidation.IsValid(handle));
    }

    [Fact]
    public void IsValid_AcceptsAndRejectsConsistentlyWithValidate()
    {
        Assert.True(HandleValidation.IsValid("alice-ai"));
        Assert.False(HandleValidation.IsValid(null));
        Assert.False(HandleValidation.IsValid(""));
    }

    [Fact]
    public void Validate_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => HandleValidation.Validate(null!));
    }

    [Fact]
    public void Constants_MatchSolidity()
    {
        Assert.Equal(3, HandleValidation.MinLength);
        Assert.Equal(32, HandleValidation.MaxLength);
    }
}
