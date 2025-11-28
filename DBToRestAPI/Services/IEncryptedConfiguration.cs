namespace DBToRestAPI.Services;

/// <summary>
/// An IConfiguration that provides access to decrypted configuration values.
/// 
/// This interface extends IConfiguration so it can be used anywhere IConfiguration is expected,
/// while also providing additional methods specific to encrypted configuration management.
/// 
/// Use this interface when you need:
/// - Access to decrypted sensitive configuration values
/// - Full IConfiguration compatibility for helper methods
/// - DI differentiation from the original IConfiguration
/// </summary>
public interface IEncryptedConfiguration : IConfiguration
{
    /// <summary>
    /// Gets a connection string, returning the decrypted value if encrypted.
    /// </summary>
    /// <param name="name">The connection string name (e.g., "default")</param>
    /// <returns>The decrypted connection string</returns>
    string? GetConnectionString(string name);

    /// <summary>
    /// Checks if a configuration key has a decrypted value cached.
    /// </summary>
    /// <param name="key">The configuration key</param>
    /// <returns>True if a decrypted value exists for the key</returns>
    bool HasDecryptedValue(string key);

    /// <summary>
    /// Gets all cached decrypted configuration paths.
    /// Useful for debugging to see what values are being managed.
    /// </summary>
    /// <returns>Collection of configuration paths with cached decrypted values</returns>
    IReadOnlyCollection<string> GetDecryptedPaths();

    /// <summary>
    /// Whether the encryption service is active (running on Windows).
    /// </summary>
    bool IsActive { get; }
}
