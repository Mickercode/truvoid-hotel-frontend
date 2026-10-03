using System.Security.Cryptography;
using System.Text;

namespace TruvoID.Infrastructure.Postgres;

/// <summary>
/// Encrypts per-Organization database passwords for storage in
/// control.organization_db_credential (AES-256-GCM). The Organization id is bound in
/// as associated data, so a ciphertext copied onto another Organization's row won't
/// decrypt. The key lives outside the database (Railway env var).
/// </summary>
public sealed class TenantCredentialProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public TenantCredentialProtector(string keyId, byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException(
                $"Postgres:TenantCredentialKey must be base64 for exactly 32 bytes (AES-256); this one is {key.Length} bytes. " +
                "Generate one with: openssl rand -base64 32 — and use the same value on the API and the worker.", nameof(key));
        KeyId = keyId;
        _key = key;
    }

    public string KeyId { get; }

    public static TenantCredentialProtector FromBase64(string keyId, string base64Key)
    {
        try
        {
            return new(keyId, Convert.FromBase64String(base64Key.Trim().Trim('"')));
        }
        catch (FormatException)
        {
            // Never echo the value: it's a secret.
            throw new ArgumentException("Postgres:TenantCredentialKey is not valid base64. Generate one with: openssl rand -base64 32");
        }
    }

    public byte[] Protect(Guid organizationId, string password)
    {
        var plaintext = Encoding.UTF8.GetBytes(password);
        var blob = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize),
            organizationId.ToByteArray());
        return blob;
    }

    public string Unprotect(Guid organizationId, string keyId, byte[] blob)
    {
        if (keyId != KeyId)
            throw new CryptographicException($"Credential was encrypted with key '{keyId}', but the active key is '{KeyId}'.");
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Credential ciphertext is truncated.");

        var plaintext = new byte[blob.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize),
            plaintext, organizationId.ToByteArray());
        return Encoding.UTF8.GetString(plaintext);
    }
}
