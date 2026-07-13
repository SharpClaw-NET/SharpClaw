using System.Security.Cryptography;
using System.Text;
using SharpClaw.Shared.Security;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public class ApiKeyEncryptorBytesTests
{
    private byte[] _key = null!;

    [SetUp]
    public void SetUp() => _key = ApiKeyEncryptor.GenerateKey();

    [Test]
    public void EncryptBytes_RoundTrips()
    {
        var original = Encoding.UTF8.GetBytes("{\"Id\":\"abc\"}");

        var encrypted = ApiKeyEncryptor.EncryptBytes(original, _key);
        var decrypted = ApiKeyEncryptor.DecryptBytes(encrypted, _key);

        decrypted.Should().Equal(original);
    }

    [Test]
    public void EncryptBytes_ProducesVersionedEnvelope()
    {
        var encrypted = ApiKeyEncryptor.EncryptBytes([1, 2, 3], _key);

        encrypted[0].Should().Be(0x01, "first byte is the version marker");
        encrypted.Length.Should().BeGreaterThanOrEqualTo(ApiKeyEncryptor.MinEnvelopeSize);
    }

    [Test]
    public void EncryptBytes_DifferentNonceEachCall()
    {
        var plain = Encoding.UTF8.GetBytes("same");

        var a = ApiKeyEncryptor.EncryptBytes(plain, _key);
        var b = ApiKeyEncryptor.EncryptBytes(plain, _key);

        a.Should().NotEqual(b, "nonce should differ between encryptions");
    }

    [Test]
    public void DecryptBytes_WrongKey_Throws()
    {
        var encrypted = ApiKeyEncryptor.EncryptBytes([42], _key);
        var wrongKey = ApiKeyEncryptor.GenerateKey();

        var act = () => ApiKeyEncryptor.DecryptBytes(encrypted, wrongKey);

        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void DecryptBytes_ShortEnvelope_Throws()
    {
        var tooShort = new byte[ApiKeyEncryptor.MinEnvelopeSize - 1];
        tooShort[0] = 0x01;

        var act = () => ApiKeyEncryptor.DecryptBytes(tooShort, _key);

        act.Should().Throw<ArgumentException>().WithMessage("*too short*");
    }

    [Test]
    public void DecryptBytes_BadVersion_Throws()
    {
        var bad = new byte[ApiKeyEncryptor.MinEnvelopeSize];
        bad[0] = 0xFF;

        var act = () => ApiKeyEncryptor.DecryptBytes(bad, _key);

        act.Should().Throw<ArgumentException>().WithMessage("*version*");
    }

    [Test]
    public void EncryptBytes_EmptyPlaintext_RoundTrips()
    {
        var encrypted = ApiKeyEncryptor.EncryptBytes([], _key);
        var decrypted = ApiKeyEncryptor.DecryptBytes(encrypted, _key);

        decrypted.Should().BeEmpty();
    }
}
