using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Com.H.Threading;
using Microsoft.Extensions.Primitives;

// Disable platform compatibility warnings - we check RuntimeInformation.IsOSPlatform at runtime
#pragma warning disable CA1416

namespace DBToRestAPI.Services;

/// <summary>
/// Service that handles encryption and decryption of sensitive configuration values using DPAPI.
/// 
/// This service:
/// - Runs only on Windows (DPAPI is Windows-specific)
/// - Automatically encrypts unencrypted sensitive values in XML config files on startup
/// - Maintains a complete merged IConfiguration copy with decrypted values
/// - Monitors for configuration changes and rebuilds the merged copy automatically
/// - Implements IEncryptedConfiguration (extends IConfiguration) for seamless integration
/// 
/// Configuration structure (in settings.xml):
/// <code>
/// &lt;settings_encryption&gt;
///   &lt;encryption_prefix&gt;encrypted:&lt;/encryption_prefix&gt;
///   &lt;sections_to_encrypt&gt;
///     &lt;section&gt;ConnectionStrings&lt;/section&gt;
///     &lt;section&gt;authorize:providers:azure_b2c&lt;/section&gt;
///     &lt;section&gt;file_management:sftp_file_store:remote_site:password&lt;/section&gt;
///   &lt;/sections_to_encrypt&gt;
/// &lt;/settings_encryption&gt;
/// </code>
/// 
/// Uses DataProtectionScope.LocalMachine so any user on the machine can decrypt
/// (suitable for IIS app pools with different identities).
/// 
/// Implements IEncryptedConfiguration which extends IConfiguration, so this service
/// can be passed to any method expecting IConfiguration while also being injectable
/// as IEncryptedConfiguration for DI differentiation.
/// </summary>
public class SettingsEncryptionService : IEncryptedConfiguration
{
    private const string DEFAULT_ENCRYPTED_PREFIX = "encrypted:";
    
    // Entropy for additional DPAPI security (like a salt)
    private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("DBToRestAPI-Secret-Sauce-2025");
    
    private readonly IConfiguration _originalConfiguration;
    private readonly ILogger<SettingsEncryptionService> _logger;
    private readonly AtomicGate _reloadingGate = new();
    
    // Complete merged IConfiguration (all original values + decrypted values overlaid)
    private IConfiguration _mergedConfiguration;
    
    // Dictionary of decrypted values only (used for overlay during merge)
    private Dictionary<string, string?> _decryptedValues = new(StringComparer.OrdinalIgnoreCase);
    
    // Tracks which sections are configured for encryption
    private HashSet<string> _sectionsToEncrypt = new(StringComparer.OrdinalIgnoreCase);
    
    // The prefix used to identify encrypted values
    private string _encryptionPrefix = DEFAULT_ENCRYPTED_PREFIX;
    
    // Whether the service is active (only on Windows)
    private readonly bool _isActive;

    public SettingsEncryptionService(
        IConfiguration configuration,
        ILogger<SettingsEncryptionService> logger)
    {
        _originalConfiguration = configuration;
        _logger = logger;
        
        // Initialize with empty merged configuration
        _mergedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        
        // Only activate on Windows (DPAPI is Windows-specific)
        _isActive = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        
        if (!_isActive)
        {
            _logger.LogInformation("Settings encryption service is disabled (not running on Windows)");
            // On non-Windows, just copy the original configuration
            RebuildMergedConfiguration();
            return;
        }
        
        // Initial load - process encryption and build merged config
        LoadAndProcessEncryption();
        
        // Monitor for ANY configuration changes (not just settings_encryption)
        ChangeToken.OnChange(
            () => _originalConfiguration.GetReloadToken(),
            LoadAndProcessEncryption);
    }

    /// <summary>
    /// Main method that loads encryption settings, encrypts unencrypted values in files,
    /// and rebuilds the complete merged configuration.
    /// </summary>
    private void LoadAndProcessEncryption()
    {
        try
        {
            if (!_reloadingGate.TryOpen()) return;
            
            _logger.LogDebug("=== Starting Settings Encryption Processing ===");
            
            // Read encryption configuration
            var encryptionSection = _originalConfiguration.GetSection("settings_encryption");
            if (!encryptionSection.Exists() || !_isActive)
            {
                _logger.LogDebug("No settings_encryption section found or not on Windows, using original config");
                _decryptedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                _sectionsToEncrypt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                RebuildMergedConfiguration();
                return;
            }
            
            // Get encryption prefix
            _encryptionPrefix = encryptionSection.GetValue<string>("encryption_prefix") ?? DEFAULT_ENCRYPTED_PREFIX;
            _logger.LogDebug("Using encryption prefix: '{Prefix}'", _encryptionPrefix);
            
            // Get sections to encrypt
            var sectionsSection = encryptionSection.GetSection("sections_to_encrypt");
            var newSectionsToEncrypt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            if (sectionsSection.Exists())
            {
                // Handle both single and multiple <section> elements (same XML quirk as ApiKeysService)
                foreach (var child in sectionsSection.GetChildren())
                {
                    var grandChildren = child.GetChildren().ToList();
                    if (grandChildren.Any())
                    {
                        // Multiple <section> elements
                        foreach (var grandChild in grandChildren)
                        {
                            if (!string.IsNullOrWhiteSpace(grandChild.Value))
                            {
                                newSectionsToEncrypt.Add(grandChild.Value);
                            }
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(child.Value))
                    {
                        // Single <section> element or direct value
                        newSectionsToEncrypt.Add(child.Value);
                    }
                }
            }
            
            _sectionsToEncrypt = newSectionsToEncrypt;
            _logger.LogDebug("Sections to encrypt: {Sections}", string.Join(", ", _sectionsToEncrypt));
            
            if (_sectionsToEncrypt.Count == 0)
            {
                _logger.LogDebug("No sections configured for encryption");
                _decryptedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                RebuildMergedConfiguration();
                return;
            }
            
            // Get list of XML files to process
            var xmlFilesToProcess = GetXmlFilesToProcess();
            _logger.LogDebug("XML files to process: {Files}", string.Join(", ", xmlFilesToProcess));
            
            // Process each XML file - collect decrypted values
            var newDecryptedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var xmlFile in xmlFilesToProcess)
            {
                ProcessXmlFile(xmlFile, newDecryptedValues);
            }
            
            _decryptedValues = newDecryptedValues;
            
            // Log all decrypted values collected
            _logger.LogDebug("=== Decrypted values collected ===");
            foreach (var kvp in _decryptedValues)
            {
                _logger.LogDebug("  '{Key}' = '{Value}'", kvp.Key, kvp.Value);
            }
            
            // Rebuild the complete merged configuration
            RebuildMergedConfiguration();
            
            _logger.LogInformation(
                "Settings encryption processing complete. Encrypted sections: {SectionCount}, Decrypted values: {ValueCount}",
                _sectionsToEncrypt.Count,
                _decryptedValues.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during settings encryption processing");
        }
        finally
        {
            _reloadingGate.TryClose();
        }
    }

    /// <summary>
    /// Rebuilds the complete merged IConfiguration by copying all values from the original
    /// configuration and overlaying with decrypted values.
    /// </summary>
    private void RebuildMergedConfiguration()
    {
        // Start with all values from original configuration
        var mergedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        
        // Recursively copy all values from original configuration
        CopyAllConfigValues(_originalConfiguration, mergedValues, "");
        
        _logger.LogDebug("=== RebuildMergedConfiguration ===");
        _logger.LogDebug("Copied {Count} values from original configuration", mergedValues.Count);
        
        // Log original api_keys_collections values before overlay
        var apiKeysBeforeOverlay = mergedValues
            .Where(kvp => kvp.Key.StartsWith("api_keys_collections:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        _logger.LogDebug("API keys from original config (before overlay): {Count}", apiKeysBeforeOverlay.Count);
        foreach (var kvp in apiKeysBeforeOverlay)
        {
            _logger.LogDebug("  Original: '{Key}' = '{Value}'", kvp.Key, kvp.Value?.Substring(0, Math.Min(30, kvp.Value?.Length ?? 0)) + "...");
        }
        
        // Overlay with decrypted values (these take precedence)
        _logger.LogDebug("Decrypted values to overlay: {Count}", _decryptedValues.Count);
        foreach (var kvp in _decryptedValues)
        {
            _logger.LogDebug("  Overlay: '{Key}' = '{Value}'", kvp.Key, kvp.Value);
            mergedValues[kvp.Key] = kvp.Value;
        }
        
        // Log final api_keys_collections values after overlay
        var apiKeysAfterOverlay = mergedValues
            .Where(kvp => kvp.Key.StartsWith("api_keys_collections:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        _logger.LogDebug("API keys in merged config (after overlay): {Count}", apiKeysAfterOverlay.Count);
        foreach (var kvp in apiKeysAfterOverlay)
        {
            _logger.LogDebug("  Final: '{Key}' = '{Value}'", kvp.Key, kvp.Value);
        }
        
        // Build the merged configuration
        _mergedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(mergedValues)
            .Build();
        
        _logger.LogDebug("Rebuilt merged configuration with {TotalCount} total values ({DecryptedCount} decrypted)", 
            mergedValues.Count, _decryptedValues.Count);
    }
    
    /// <summary>
    /// Recursively copies all configuration values to a dictionary.
    /// </summary>
    private static void CopyAllConfigValues(IConfiguration config, Dictionary<string, string?> target, string parentPath)
    {
        foreach (var child in config.GetChildren())
        {
            var path = string.IsNullOrEmpty(parentPath) ? child.Key : $"{parentPath}:{child.Key}";
            
            if (child.Value != null)
            {
                target[path] = child.Value;
            }
            
            // Recurse into children
            CopyAllConfigValues(child, target, path);
        }
    }

    /// <summary>
    /// Gets the list of XML configuration files to process.
    /// </summary>
    private List<string> GetXmlFilesToProcess()
    {
        var files = new List<string>();
        var basePath = AppContext.BaseDirectory;
        
        // Always include settings.xml
        var settingsPath = Path.Combine(basePath, "config", "settings.xml");
        if (File.Exists(settingsPath))
        {
            files.Add(settingsPath);
        }
        
        // Add files from additional_configurations:path
        var pathsSection = _originalConfiguration.GetSection("additional_configurations:path");
        if (pathsSection.Exists())
        {
            foreach (var child in pathsSection.GetChildren())
            {
                // Handle both single and multiple path elements
                var grandChildren = child.GetChildren().ToList();
                if (grandChildren.Any())
                {
                    foreach (var grandChild in grandChildren)
                    {
                        AddXmlFileIfExists(files, basePath, grandChild.Value);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(child.Value))
                {
                    AddXmlFileIfExists(files, basePath, child.Value);
                }
            }
        }
        
        return files;
    }

    private void AddXmlFileIfExists(List<string> files, string basePath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        
        // Only process XML files
        if (!relativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return;
        
        var fullPath = Path.Combine(basePath, relativePath);
        if (File.Exists(fullPath) && !files.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(fullPath);
        }
    }

    /// <summary>
    /// Processes a single XML file: encrypts unencrypted values and caches decrypted values.
    /// </summary>
    private void ProcessXmlFile(string filePath, Dictionary<string, string?> decryptedValues)
    {
        try
        {
            _logger.LogDebug("Processing XML file: {FilePath}", filePath);
            
            // Load with PreserveWhitespace to maintain comments, formatting, and whitespace
            var doc = XDocument.Load(filePath, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root == null) return;
            
            bool fileModified = false;
            
            foreach (var sectionPath in _sectionsToEncrypt)
            {
                // Convert configuration path to XML path
                // e.g., "ConnectionStrings:default" -> ["ConnectionStrings", "default"]
                var pathParts = sectionPath.Split(':');
                
                // Try to find matching elements in the XML
                var matchingElements = FindMatchingElements(root, pathParts, 0);
                
                foreach (var (element, configPath) in matchingElements)
                {
                    fileModified |= ProcessElement(element, configPath, decryptedValues);
                }
            }
            
            // Save file if any values were encrypted
            if (fileModified)
            {
                _logger.LogInformation("Saving encrypted values to: {FilePath}", filePath);
                // Save with DisableFormatting to preserve original whitespace and formatting
                doc.Save(filePath, SaveOptions.DisableFormatting);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing XML file: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Recursively finds elements matching the configuration path.
    /// </summary>
    private List<(XElement Element, string ConfigPath)> FindMatchingElements(
        XElement current, 
        string[] pathParts, 
        int index,
        string currentPath = "")
    {
        var results = new List<(XElement, string)>();
        
        if (index >= pathParts.Length)
        {
            // We've matched all parts, return this element
            results.Add((current, currentPath.TrimStart(':')));
            return results;
        }
        
        var targetName = pathParts[index];
        var matchingChildren = current.Elements()
            .Where(e => e.Name.LocalName.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        
        foreach (var child in matchingChildren)
        {
            var childPath = currentPath + ":" + child.Name.LocalName;
            results.AddRange(FindMatchingElements(child, pathParts, index + 1, childPath));
        }
        
        return results;
    }

    /// <summary>
    /// Processes an XML element: encrypts if unencrypted, decrypts and caches if encrypted.
    /// Handles both leaf elements and parent elements (encrypts all children).
    /// Also handles multiple elements with the same name by adding index suffix (e.g., key:0, key:1).
    /// </summary>
    private bool ProcessElement(XElement element, string basePath, Dictionary<string, string?> decryptedValues)
    {
        bool modified = false;
        
        // If element has children, process each child recursively
        if (element.HasElements)
        {
            // Group children by name to handle multiple elements with the same name
            var childGroups = element.Elements().GroupBy(e => e.Name.LocalName);
            
            foreach (var group in childGroups)
            {
                var childrenList = group.ToList();
                
                if (childrenList.Count == 1)
                {
                    // Single element - no index needed
                    var child = childrenList[0];
                    var childPath = string.IsNullOrEmpty(basePath) 
                        ? child.Name.LocalName 
                        : $"{basePath}:{child.Name.LocalName}";
                    modified |= ProcessElement(child, childPath, decryptedValues);
                }
                else
                {
                    // Multiple elements with same name - add index (matches IConfiguration behavior)
                    for (int i = 0; i < childrenList.Count; i++)
                    {
                        var child = childrenList[i];
                        var childPath = string.IsNullOrEmpty(basePath) 
                            ? $"{child.Name.LocalName}:{i}" 
                            : $"{basePath}:{child.Name.LocalName}:{i}";
                        modified |= ProcessElement(child, childPath, decryptedValues);
                    }
                }
            }
        }
        else
        {
            // Leaf element with a value
            var value = element.Value;
            if (string.IsNullOrEmpty(value)) return false;
            
            if (IsEncrypted(value))
            {
                // Already encrypted - decrypt and cache
                var decryptedValue = Decrypt(value);
                decryptedValues[basePath] = decryptedValue;
                _logger.LogDebug("Cached decrypted value for: {Path}", basePath);
            }
            else
            {
                // Not encrypted - encrypt and save, then cache decrypted
                var encryptedValue = Encrypt(value);
                element.Value = encryptedValue;
                decryptedValues[basePath] = value;
                modified = true;
                _logger.LogDebug("Encrypted value for: {Path}", basePath);
            }
        }
        
        return modified;
    }

    /// <summary>
    /// Encrypts a plain text value using DPAPI.
    /// </summary>
    private string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;
        
        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            
            // Encrypt using DPAPI
            // DataProtectionScope.LocalMachine = any user on this machine can decrypt
            byte[] encryptedBytes = ProtectedData.Protect(
                plainBytes,
                _entropy,
                DataProtectionScope.LocalMachine
            );
            
            // Convert to Base64 and add prefix
            return _encryptionPrefix + Convert.ToBase64String(encryptedBytes);
        }
        catch (Exception ex)
        {
            throw new CryptographicException($"Encryption failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Decrypts an encrypted value using DPAPI.
    /// </summary>
    private string Decrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
            return encryptedText;
        
        // Check if value is actually encrypted
        if (!IsEncrypted(encryptedText))
            return encryptedText;
        
        try
        {
            // Remove prefix and convert from Base64
            string base64 = encryptedText[_encryptionPrefix.Length..];
            byte[] encryptedBytes = Convert.FromBase64String(base64);
            
            // Decrypt using DPAPI
            byte[] plainBytes = ProtectedData.Unprotect(
                encryptedBytes,
                _entropy,
                DataProtectionScope.LocalMachine
            );
            
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            throw new CryptographicException($"Decryption failed for value. This may indicate the value was encrypted on a different machine or the DPAPI keys have been lost: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Checks if a value is encrypted (has the encryption prefix).
    /// </summary>
    private bool IsEncrypted(string value)
    {
        return !string.IsNullOrEmpty(value) && value.StartsWith(_encryptionPrefix);
    }

    #region IConfiguration Implementation

    /// <summary>
    /// Gets or sets a configuration value.
    /// When getting: returns decrypted value if available, falls back to main configuration.
    /// When setting: sets the value in the decrypted configuration (not persisted).
    /// </summary>
    public string? this[string key]
    {
        get => _mergedConfiguration[key];
        set
        {
            // Setting values updates the in-memory decrypted values (not persisted)
            // This maintains IConfiguration contract but changes won't survive restart
            if (!string.IsNullOrWhiteSpace(key))
            {
                _decryptedValues[key] = value;
                RebuildMergedConfiguration();
            }
        }
    }

    /// <summary>
    /// Gets the immediate descendant configuration sub-sections from the merged configuration.
    /// Sections are wrapped to preserve reload token behavior from the original configuration.
    /// </summary>
    public IEnumerable<IConfigurationSection> GetChildren()
    {
        var mergedChildren = _mergedConfiguration.GetChildren().ToList();
        var originalChildren = _originalConfiguration.GetChildren().ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var mergedChild in mergedChildren)
        {
            if (originalChildren.TryGetValue(mergedChild.Key, out var originalChild))
            {
                yield return new ConfigurationSectionWrapper(mergedChild, originalChild);
            }
            else
            {
                yield return new ConfigurationSectionWrapper(mergedChild, mergedChild);
            }
        }
    }

    /// <summary>
    /// Returns a change token that can be used to observe when this configuration is reloaded.
    /// </summary>
    public IChangeToken GetReloadToken()
    {
        // Return the original configuration's reload token since that's what triggers our reload
        return _originalConfiguration.GetReloadToken();
    }

    #endregion

    #region Public API

    /// <summary>
    /// Gets a configuration value from the merged configuration.
    /// </summary>
    /// <param name="key">The configuration key (e.g., "ConnectionStrings:default")</param>
    /// <returns>The configuration value (decrypted if it was encrypted)</returns>
    public string? GetValue(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        
        return _mergedConfiguration.GetValue<string>(key);
    }

    /// <summary>
    /// Gets a configuration value as the specified type from the merged configuration.
    /// </summary>
    /// <typeparam name="T">The type to convert the value to</typeparam>
    /// <param name="key">The configuration key</param>
    /// <returns>The converted value, or default(T) if not found</returns>
    public T? GetValue<T>(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return default;
        
        return _mergedConfiguration.GetValue<T>(key);
    }

    /// <summary>
    /// Gets a configuration section from the merged configuration.
    /// The merged configuration contains all original values overlaid with decrypted values.
    /// The returned section uses the original configuration's reload token so that
    /// ChangeToken.OnChange() works correctly even though data comes from the merged config.
    /// </summary>
    /// <param name="key">The section key (e.g., "ConnectionStrings" or "api_keys_collections")</param>
    /// <returns>An IConfigurationSection wrapper that combines merged data with original reload tokens</returns>
    public IConfigurationSection GetSection(string key)
    {
        var mergedSection = _mergedConfiguration.GetSection(key);
        var originalSection = _originalConfiguration.GetSection(key);
        return new ConfigurationSectionWrapper(mergedSection, originalSection);
    }
    
    /// <summary>
    /// Helper method to get all key-value pairs under a configuration section recursively.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string?>> GetAllValuesUnderSection(IConfiguration config, string sectionKey)
    {
        var section = config.GetSection(sectionKey);
        return GetAllValuesRecursive(section, sectionKey);
    }
    
    private static IEnumerable<KeyValuePair<string, string?>> GetAllValuesRecursive(IConfigurationSection section, string currentPath)
    {
        // If this section has a value, yield it
        if (section.Value != null)
        {
            yield return new KeyValuePair<string, string?>(currentPath, section.Value);
        }
        
        // Recurse into children
        foreach (var child in section.GetChildren())
        {
            var childPath = $"{currentPath}:{child.Key}";
            foreach (var kvp in GetAllValuesRecursive(child, childPath))
            {
                yield return kvp;
            }
        }
    }

    /// <summary>
    /// Gets a connection string, returning the decrypted value if encrypted.
    /// This is a convenience method for the common case of encrypted connection strings.
    /// </summary>
    /// <param name="name">The connection string name (e.g., "default")</param>
    /// <returns>The decrypted connection string</returns>
    public string? GetConnectionString(string name)
    {
        return _mergedConfiguration.GetConnectionString(name);
    }

    /// <summary>
    /// Gets all decrypted values under a parent path.
    /// Useful for sections with multiple child elements (e.g., "nested_secret_data" returns all secrets).
    /// </summary>
    /// <param name="parentPath">The parent configuration path</param>
    /// <returns>Dictionary of child paths (relative to parent) and their decrypted values</returns>
    public IReadOnlyDictionary<string, string?> GetValuesUnderPath(string parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            return new Dictionary<string, string?>();
        
        var prefix = parentPath.EndsWith(':') ? parentPath : parentPath + ":";
        
        return _decryptedValues
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                kvp => kvp.Key[prefix.Length..], // Return relative path
                kvp => kvp.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets all decrypted values under a parent path as a list.
    /// Useful when you just need the values without caring about the exact paths.
    /// </summary>
    /// <param name="parentPath">The parent configuration path</param>
    /// <returns>List of decrypted values under the parent path</returns>
    public IReadOnlyList<string?> GetValueListUnderPath(string parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            return new List<string?>();
        
        var prefix = parentPath.EndsWith(':') ? parentPath : parentPath + ":";
        
        return _decryptedValues
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Checks if a configuration key has a decrypted value cached.
    /// </summary>
    /// <param name="key">The configuration key</param>
    /// <returns>True if a decrypted value exists for the key</returns>
    public bool HasDecryptedValue(string key)
    {
        return !string.IsNullOrWhiteSpace(key) && _decryptedValues.ContainsKey(key);
    }

    /// <summary>
    /// Gets all cached decrypted configuration paths.
    /// Useful for debugging to see what values are being managed.
    /// </summary>
    /// <returns>Collection of configuration paths with cached decrypted values</returns>
    public IReadOnlyCollection<string> GetDecryptedPaths()
    {
        return _decryptedValues.Keys.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets the merged in-memory IConfiguration containing all original values overlaid with decrypted values.
    /// Use this if you need direct access to the merged configuration.
    /// </summary>
    public IConfiguration MergedConfiguration => _mergedConfiguration;

    /// <summary>
    /// Whether the encryption service is active (running on Windows).
    /// </summary>
    public bool IsActive => _isActive;

    #endregion
}

/// <summary>
/// A wrapper around IConfigurationSection that delegates data operations to a merged configuration
/// but returns reload tokens from the original configuration.
/// 
/// This is necessary because the merged configuration is an in-memory IConfiguration
/// whose sections don't have proper reload tokens. By wrapping sections, we can:
/// - Read data from the merged config (which has decrypted values)
/// - Return reload tokens from the original config (which fires when XML files change)
/// 
/// This allows code like:
///   ChangeToken.OnChange(() => config.GetSection("api_keys").GetReloadToken(), LoadKeys);
/// to work correctly even when using IEncryptedConfiguration.
/// </summary>
internal class ConfigurationSectionWrapper : IConfigurationSection
{
    private readonly IConfigurationSection _mergedSection;
    private readonly IConfigurationSection _originalSection;

    public ConfigurationSectionWrapper(IConfigurationSection mergedSection, IConfigurationSection originalSection)
    {
        _mergedSection = mergedSection;
        _originalSection = originalSection;
    }

    /// <summary>
    /// Gets the key this section occupies in its parent.
    /// </summary>
    public string Key => _mergedSection.Key;

    /// <summary>
    /// Gets the full path to this section within the configuration.
    /// </summary>
    public string Path => _mergedSection.Path;

    /// <summary>
    /// Gets or sets the section value (from merged/decrypted config).
    /// </summary>
    public string? Value
    {
        get => _mergedSection.Value;
        set => _mergedSection.Value = value;
    }

    /// <summary>
    /// Gets or sets a configuration value (from merged/decrypted config).
    /// </summary>
    public string? this[string key]
    {
        get => _mergedSection[key];
        set => _mergedSection[key] = value;
    }

    /// <summary>
    /// Returns a change token from the ORIGINAL configuration.
    /// This is the key method - it ensures ChangeToken.OnChange works correctly.
    /// </summary>
    public IChangeToken GetReloadToken()
    {
        // Return the original config's token so changes are detected
        return _originalSection.GetReloadToken();
    }

    /// <summary>
    /// Gets a subsection with the specified key, wrapped to preserve reload token behavior.
    /// </summary>
    public IConfigurationSection GetSection(string key)
    {
        var mergedChild = _mergedSection.GetSection(key);
        var originalChild = _originalSection.GetSection(key);
        return new ConfigurationSectionWrapper(mergedChild, originalChild);
    }

    /// <summary>
    /// Gets the immediate descendant configuration sub-sections, wrapped.
    /// </summary>
    public IEnumerable<IConfigurationSection> GetChildren()
    {
        // Get children from merged config (has decrypted values)
        // But we need to wrap them to preserve reload token behavior
        var mergedChildren = _mergedSection.GetChildren().ToList();
        var originalChildren = _originalSection.GetChildren().ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var mergedChild in mergedChildren)
        {
            // Try to find matching original section for reload token
            if (originalChildren.TryGetValue(mergedChild.Key, out var originalChild))
            {
                yield return new ConfigurationSectionWrapper(mergedChild, originalChild);
            }
            else
            {
                // No original section (shouldn't happen normally, but handle gracefully)
                // Use merged section for both - reload token won't work but data will
                yield return new ConfigurationSectionWrapper(mergedChild, mergedChild);
            }
        }
    }
}
