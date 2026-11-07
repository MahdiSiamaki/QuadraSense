using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Sqm.Infrastructure.Identity;

/// <summary>Seals an authenticator secret for storage, under a key the database does not hold.</summary>
/// <remarks>
/// <para>
/// AES-256-GCM, which authenticates as well as encrypts: a changed byte fails to open rather than
/// opening to the wrong secret. The user id is authenticated with it, so a row copied onto another
/// account does not open either.
/// </para>
/// <para>
/// A key in configuration rather than ASP.NET's Data Protection, because that wants a key ring on
/// a writable, shared volume, and the containers here have a read-only root and may run as
/// several replicas (ADR-007). One value in the environment needs neither.
/// </para>
/// </remarks>
internal sealed class SecretBox
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    private readonly byte[] _key;

    private SecretBox(byte[] key) => _key = key;

    /// <summary>The box for this key, or <see langword="null"/> when none is configured.</summary>
    /// <exception cref="InvalidOperationException">The key is there and unusable.</exception>
    public static SecretBox? FromConfiguration(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            return null;
        }

        byte[] key;

        try
        {
            key = Convert.FromBase64String(base64Key.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Auth:TwoStep:SecretKey is not base64.", ex);
        }

        return key.Length == KeyLength
            ? new SecretBox(key)
            : throw new InvalidOperationException(
                $"Auth:TwoStep:SecretKey must be {KeyLength} bytes. Generate one with: "
                + "openssl rand -base64 32");
    }

    /// <summary>Nonce, tag and ciphertext, in that order.</summary>
    public byte[] Seal(ReadOnlySpan<byte> secret, long userId)
    {
        var box = new byte[NonceLength + TagLength + secret.Length];
        var nonce = box.AsSpan(0, NonceLength);
        var tag = box.AsSpan(NonceLength, TagLength);
        var cipher = box.AsSpan(NonceLength + TagLength);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagLength);
        aes.Encrypt(nonce, secret, cipher, tag, Associated(userId));

        return box;
    }

    /// <summary>The secret, or <see langword="null"/> if this key and user cannot open it.</summary>
    public byte[]? Open(ReadOnlySpan<byte> box, long userId)
    {
        if (box.Length <= NonceLength + TagLength)
        {
            return null;
        }

        var secret = new byte[box.Length - NonceLength - TagLength];

        try
        {
            using var aes = new AesGcm(_key, TagLength);
            aes.Decrypt(
                box[..NonceLength],
                box[(NonceLength + TagLength)..],
                box.Slice(NonceLength, TagLength),
                secret,
                Associated(userId));

            return secret;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    private static byte[] Associated(long userId)
    {
        var data = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(data, userId);
        return data;
    }
}
