// Educational and personal use only.

using FluentAssertions;
using TermReader.Domain.Entities.Credentials;
using TermReader.Domain.Enums;
using Xunit;

namespace TermReader.Tests;

[Trait("Category", "Unit")]
public class SiteCredentialTests
{
    private static readonly byte[] ValidUsername = new byte[] { 1, 2, 3, 4 };
    private static readonly byte[] ValidPassword = new byte[] { 5, 6, 7, 8 };

    #region Create

    [Fact]
    public void Create_WithValidData_SetsPropertiesAndGeneratesId()
    {
        var credential = SiteCredential.Create(
            "nytimes.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        credential.Id.Should().NotBe(Guid.Empty);
        credential.Domain.Should().Be("nytimes.com");
        credential.CredentialType.Should().Be(CredentialType.FormLogin);
        credential.EncryptedUsername.Should().BeEquivalentTo(ValidUsername);
        credential.EncryptedPassword.Should().BeEquivalentTo(ValidPassword);
        credential.UsernameSelector.Should().BeNull();
        credential.PasswordSelector.Should().BeNull();
        credential.SubmitSelector.Should().BeNull();
        credential.LoginUrl.Should().BeNull();
        credential.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        credential.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Create_WithAllOptionalFields_SetsAllProperties()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword,
            usernameSelector: "#email",
            passwordSelector: "#password",
            submitSelector: "button[type=submit]",
            loginUrl: "https://example.com/login");

        credential.UsernameSelector.Should().Be("#email");
        credential.PasswordSelector.Should().Be("#password");
        credential.SubmitSelector.Should().Be("button[type=submit]");
        credential.LoginUrl.Should().Be("https://example.com/login");
    }

    [Fact]
    public void Create_NormalizesDomainToLowerCase()
    {
        var credential = SiteCredential.Create(
            "NYTimes.COM",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        credential.Domain.Should().Be("nytimes.com");
    }

    [Fact]
    public void Create_TrimsDomainWhitespace()
    {
        var credential = SiteCredential.Create(
            "  nytimes.com  ",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        credential.Domain.Should().Be("nytimes.com");
    }

    [Fact]
    public void Create_TrimsOptionalSelectors()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword,
            usernameSelector: "  #email  ",
            passwordSelector: "  #pass  ",
            submitSelector: "  #submit  ",
            loginUrl: "  https://example.com/login  ");

        credential.UsernameSelector.Should().Be("#email");
        credential.PasswordSelector.Should().Be("#pass");
        credential.SubmitSelector.Should().Be("#submit");
        credential.LoginUrl.Should().Be("https://example.com/login");
    }

    [Fact]
    public void Create_WithBasicAuthType_SetsCredentialType()
    {
        var credential = SiteCredential.Create(
            "secure.example.com",
            CredentialType.BasicAuth,
            ValidUsername,
            ValidPassword);

        credential.CredentialType.Should().Be(CredentialType.BasicAuth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithInvalidDomain_ThrowsArgumentException(string? domain)
    {
        var act = () => SiteCredential.Create(
            domain!,
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        act.Should().Throw<ArgumentException>().WithParameterName("domain");
    }

    [Fact]
    public void Create_WithNullEncryptedUsername_ThrowsArgumentException()
    {
        var act = () => SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            null!,
            ValidPassword);

        act.Should().Throw<ArgumentException>().WithParameterName("encryptedUsername");
    }

    [Fact]
    public void Create_WithEmptyEncryptedUsername_ThrowsArgumentException()
    {
        var act = () => SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            Array.Empty<byte>(),
            ValidPassword);

        act.Should().Throw<ArgumentException>().WithParameterName("encryptedUsername");
    }

    [Fact]
    public void Create_WithNullEncryptedPassword_ThrowsArgumentException()
    {
        var act = () => SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            null!);

        act.Should().Throw<ArgumentException>().WithParameterName("encryptedPassword");
    }

    [Fact]
    public void Create_WithEmptyEncryptedPassword_ThrowsArgumentException()
    {
        var act = () => SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            Array.Empty<byte>());

        act.Should().Throw<ArgumentException>().WithParameterName("encryptedPassword");
    }

    [Fact]
    public void Create_GeneratesUniqueIds()
    {
        var c1 = SiteCredential.Create("one.com", CredentialType.FormLogin, ValidUsername, ValidPassword);
        var c2 = SiteCredential.Create("two.com", CredentialType.FormLogin, ValidUsername, ValidPassword);

        c1.Id.Should().NotBe(c2.Id);
    }

    #endregion

    #region Update

    [Fact]
    public void Update_ChangesEncryptedFieldsAndUpdatesTimestamp()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        var originalUpdatedAt = credential.UpdatedAt;

        // Small delay to ensure timestamp difference
        var newUsername = new byte[] { 10, 20, 30 };
        var newPassword = new byte[] { 40, 50, 60 };

        credential.Update(newUsername, newPassword);

        credential.EncryptedUsername.Should().BeEquivalentTo(newUsername);
        credential.EncryptedPassword.Should().BeEquivalentTo(newPassword);
        credential.UpdatedAt.Should().BeOnOrAfter(originalUpdatedAt);
    }

    [Fact]
    public void Update_SetsOptionalSelectors()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        var newUsername = new byte[] { 10, 20, 30 };
        var newPassword = new byte[] { 40, 50, 60 };

        credential.Update(
            newUsername,
            newPassword,
            usernameSelector: "#user",
            passwordSelector: "#pass",
            submitSelector: "#go",
            loginUrl: "https://example.com/auth");

        credential.UsernameSelector.Should().Be("#user");
        credential.PasswordSelector.Should().Be("#pass");
        credential.SubmitSelector.Should().Be("#go");
        credential.LoginUrl.Should().Be("https://example.com/auth");
    }

    [Fact]
    public void Update_ClearsOptionalSelectorsWhenNull()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword,
            usernameSelector: "#old",
            passwordSelector: "#old-pass");

        var newUsername = new byte[] { 10, 20, 30 };
        var newPassword = new byte[] { 40, 50, 60 };

        credential.Update(newUsername, newPassword);

        credential.UsernameSelector.Should().BeNull();
        credential.PasswordSelector.Should().BeNull();
        credential.SubmitSelector.Should().BeNull();
        credential.LoginUrl.Should().BeNull();
    }

    [Fact]
    public void Update_DoesNotChangeDomainOrCreatedAt()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        var originalDomain = credential.Domain;
        var originalCreatedAt = credential.CreatedAt;

        credential.Update(new byte[] { 10, 20 }, new byte[] { 30, 40 });

        credential.Domain.Should().Be(originalDomain);
        credential.CreatedAt.Should().Be(originalCreatedAt);
    }

    [Fact]
    public void Update_WithNullEncryptedUsername_ThrowsArgumentException()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        var act = () => credential.Update(null!, ValidPassword);

        act.Should().Throw<ArgumentException>().WithParameterName("encryptedUsername");
    }

    [Fact]
    public void Update_WithEmptyEncryptedUsername_ThrowsArgumentException()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        var act = () => credential.Update(Array.Empty<byte>(), ValidPassword);

        act.Should().Throw<ArgumentException>().WithParameterName("encryptedUsername");
    }

    [Fact]
    public void Update_WithNullEncryptedPassword_ThrowsArgumentException()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        var act = () => credential.Update(ValidUsername, null!);

        act.Should().Throw<ArgumentException>().WithParameterName("encryptedPassword");
    }

    [Fact]
    public void Update_WithEmptyEncryptedPassword_ThrowsArgumentException()
    {
        var credential = SiteCredential.Create(
            "example.com",
            CredentialType.FormLogin,
            ValidUsername,
            ValidPassword);

        var act = () => credential.Update(ValidUsername, Array.Empty<byte>());

        act.Should().Throw<ArgumentException>().WithParameterName("encryptedPassword");
    }

    #endregion
}
