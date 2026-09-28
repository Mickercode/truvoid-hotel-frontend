using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void HashesAreSaltedAndVerifyTheOriginalPassword()
    {
        var first = PasswordHasher.Hash("correct horse battery staple");
        var second = PasswordHasher.Hash("correct horse battery staple");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("correct horse battery staple", first));
        Assert.False(PasswordHasher.Verify("wrong password", first));
    }

    [Fact]
    public void InvalidEncodedValuesFailClosed()
    {
        Assert.False(PasswordHasher.Verify("password", "sha256$not-a-valid-hash"));
        Assert.False(PasswordHasher.Verify("password", ""));
    }
}
