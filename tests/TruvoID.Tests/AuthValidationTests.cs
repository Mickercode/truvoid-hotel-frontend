using TruvoID.API.Endpoints;

namespace TruvoID.Tests;

public class AuthValidationTests
{
    [Theory]
    [InlineData("Passw0rd")]
    [InlineData("correct horse battery 9")]
    public void ValidatePassword_AcceptsLetterAndNumberWithMinLength(string password) =>
        Assert.Null(AuthValidation.ValidatePassword(password));

    [Theory]
    [InlineData(null, "at least 8")]
    [InlineData("", "at least 8")]
    [InlineData("abc123", "at least 8")]
    [InlineData("password", "letter and one number")]
    [InlineData("12345678", "letter and one number")]
    public void ValidatePassword_RejectsWeakPasswords(string? password, string expected) =>
        Assert.Contains(expected, AuthValidation.ValidatePassword(password));

    [Fact]
    public void ValidatePassword_RejectsOverlongPasswords() =>
        Assert.Contains("at most 128", AuthValidation.ValidatePassword(new string('a', 128) + "1"));

    [Theory]
    [InlineData("admin@bank.ng")]
    [InlineData(" Admin@Bank.co.uk ")]
    public void IsValidEmail_AcceptsNormalAddresses(string email) =>
        Assert.True(AuthValidation.IsValidEmail(email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@bank.ng")]
    [InlineData("a@b@bank.ng")]
    [InlineData("admin@bank")]
    [InlineData("admin@.ng")]
    [InlineData("admin@bank.")]
    [InlineData("ad min@bank.ng")]
    public void IsValidEmail_RejectsMalformedAddresses(string? email) =>
        Assert.False(AuthValidation.IsValidEmail(email));

    [Fact]
    public void ValidateRegistration_AcceptsCompleteRequest() =>
        Assert.Null(AuthValidation.ValidateRegistration("First Bank", "Ada Obi", "ada@firstbank.ng", "Secur3pass"));

    [Theory]
    [InlineData("A", "Ada Obi", "ada@firstbank.ng", "Secur3pass", "Institution name")]
    [InlineData("First Bank", " ", "ada@firstbank.ng", "Secur3pass", "full name")]
    [InlineData("First Bank", "Ada Obi", "not-an-email", "Secur3pass", "email")]
    [InlineData("First Bank", "Ada Obi", "ada@firstbank.ng", "short1", "at least 8")]
    public void ValidateRegistration_ReportsFirstInvalidField(string org, string admin, string email, string password, string expected) =>
        Assert.Contains(expected, AuthValidation.ValidateRegistration(org, admin, email, password));
}
