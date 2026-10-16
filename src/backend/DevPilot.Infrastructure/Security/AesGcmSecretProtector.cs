using System.Security.Cryptography;
using System.Text;
using DevPilot.Application.Common;
using Microsoft.Extensions.Options;

namespace DevPilot.Infrastructure.Security;

public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    /// <summary>Base64-encoded 32-byte key (AES-256). Supply via environment/secret store; never commit a production key.</summary>
    public string? EncryptionKey { get; set; }

    /// <summary>Identifier stored with each ciphertext so keys can be rotated (old ids kept in <see cref="PreviousKeys"/>).</summary>
    public string KeyId { get; set; } = "k1";

    public Dictionary<string, string> PreviousKeys { get; set; } = [];
}

/// <summary>
/// AES-256-GCM authenticated encryption. Format: "{keyId}:{base64(nonce|tag|ciphertext)}". Tampering or a wrong key
/// fails decryption instead of returning garbage.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _keyId;
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public AesGcmSecretProtector(IOptions<SecretsOptions> options)
    {
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.EncryptionKey))
        {
            throw new InvalidOperationException("Secrets:EncryptionKey is not configured (base64, 32 bytes).");
        }

        _keyId = value.KeyId;
        _keys[_keyId] = Decode(value.EncryptionKey);
        foreach (var (id, key) in value.PreviousKeys)
        {
            _keys.TryAdd(id, Decode(key));
        }
    }

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var buffer = new byte[NonceSize + TagSize + plain.Length];
        var nonce = buffer.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_keys[_keyId], TagSize);
        aes.Encrypt(nonce, plain, buffer.AsSpan(NonceSize + TagSize), buffer.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(_keyId));
        return $"{_keyId}:{Convert.ToBase64String(buffer)}";
    }

    public string Unprotect(string protectedValue)
    {
        var separator = protectedValue.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || !_keys.TryGetValue(protectedValue[..separator], out var key))
        {
            throw new CryptographicException("Unknown key id for protected secret.");
        }

        var keyId = protectedValue[..separator];
        var buffer = Convert.FromBase64String(protectedValue[(separator + 1)..]);
        if (buffer.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Protected secret is malformed.");
        }

        var plain = new byte[buffer.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(buffer.AsSpan(0, NonceSize), buffer.AsSpan(NonceSize + TagSize), buffer.AsSpan(NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(keyId));
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Decode(string base64)
    {
        var key = Convert.FromBase64String(base64);
        return key.Length == 32 ? key : throw new InvalidOperationException("Secrets:EncryptionKey must decode to exactly 32 bytes.");
    }
}
