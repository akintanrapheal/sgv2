using Microsoft.AspNetCore.Identity;
using SterlingLams.Web.Infrastructure.Auth;
using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

public class WordPressPasswordHasherTests
{
    private readonly WordPressPasswordHasher _hasher = new();
    private readonly ApplicationUser _user = new();

    // Real WordPress-format hashes of the password below, generated independently (passlib / bcrypt).
    private const string Password = "Sterlin@2026!";
    private const string PhpassHash = "$P$HF.crx0szLIYriJJ.aspmlgOKUPyEE0";
    private const string BcryptHash = "$2b$10$qsBhqFon1V8Fak149pE5CeDcRDConH8BinDFEr4NRE2jyH/jwOzcu";

    [Fact]
    public void Phpass_correct_password_succeeds_and_asks_for_rehash()
        => Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded,
            _hasher.VerifyHashedPassword(_user, PhpassHash, Password));

    [Fact]
    public void Phpass_wrong_password_fails()
        => Assert.Equal(PasswordVerificationResult.Failed,
            _hasher.VerifyHashedPassword(_user, PhpassHash, "wrong-password"));

    [Fact]
    public void Bcrypt_correct_password_succeeds_and_asks_for_rehash()
        => Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded,
            _hasher.VerifyHashedPassword(_user, BcryptHash, Password));

    [Fact]
    public void Bcrypt_wrong_password_fails()
        => Assert.Equal(PasswordVerificationResult.Failed,
            _hasher.VerifyHashedPassword(_user, BcryptHash, "wrong-password"));

    [Fact]
    public void Native_identity_hash_round_trips_without_rehash()
    {
        var native = _hasher.HashPassword(_user, Password);      // modern PBKDF2 hash
        Assert.StartsWith("A", native[..1]);                     // Identity v3 hashes are base64, not "$..."
        Assert.Equal(PasswordVerificationResult.Success,
            _hasher.VerifyHashedPassword(_user, native, Password));
        Assert.Equal(PasswordVerificationResult.Failed,
            _hasher.VerifyHashedPassword(_user, native, "wrong-password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$P$")]                  // truncated phpass
    [InlineData("$2y$not-a-real-hash")]  // malformed bcrypt (must not throw)
    public void Malformed_or_empty_hashes_fail_gracefully(string hash)
        => Assert.Equal(PasswordVerificationResult.Failed,
            _hasher.VerifyHashedPassword(_user, hash, Password));
}
