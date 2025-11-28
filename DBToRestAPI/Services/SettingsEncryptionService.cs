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
/// - Maintains decrypted values in an in-memory IConfiguration for seamless access
/// - Monitors for configuration changes and reloads automatically
/// - Provides GetSection() support just like IConfiguration
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
    
    private readonly IConfiguration _configuration;
    private readonly ILogger<SettingsEncryptionService> _logger;
    private readonly AtomicGate _reloadingGate = new();
    
    // In-memory IConfiguration containing only decrypted values
    private IConfiguration _decryptedConfiguration;
    
    // Dictionary for quick key lookups (also used to build the in-memory config)
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
        _configuration = configuration;
        _logger = logger;
        
        // Initialize empty in-memory configuration
        _decryptedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        
        // Only activate on Windows (DPAPI is Windows-specific)
        _isActive = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        
        if (!_isActive)
        {
            _logger.LogInformation("Settings encryption service is disabled (not running on Windows)");
            return;
        }
        
        // Initial load
        LoadAndProcessEncryption();
        
        // Monitor for changes to the settings_encryption section
        ChangeToken.OnChange(
            () => _configuration.GetSection("settings_encryption").GetReloadToken(),
            LoadAndProcessEncryption);
    }

    /// <summary>
    /// Main method that loads encryption settings, encrypts unencrypted values in files,
    /// and populates the in-memory decrypted cache.
    /// </summary>
    private void LoadAndProcessEncryption()
    {
        if (!_isActive) return;
        
        try
        {
            if (!_reloadingGate.TryOpen()) return;
            
            _logger.LogDebug("=== Starting Settings Encryption Processing ===");
            
            // Read encryption configuration
            var encryptionSection = _configuration.GetSection("settings_encryption");
            if (!encryptionSection.Exists())
            {
                _logger.LogDebug("No settings_encryption section found, skipping encryption processing");
                _decryptedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                _sectionsToEncrypt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                RebuildInMemoryConfiguration();
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
                RebuildInMemoryConfiguration();
                return;
            }
            
            // Get list of XML files to process
            var xmlFilesToProcess = GetXmlFilesToProcess();
            _logger.LogDebug("XML files to process: {Files}", string.Join(", ", xmlFilesToProcess));
            
            // Process each XML file
            var newDecryptedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var xmlFile in xmlFilesToProcess)
            {
                ProcessXmlFile(xmlFile, newDecryptedValues);
            }
            
            _decryptedValues = newDecryptedValues;
            
            // Rebuild the in-memory IConfiguration with the new decrypted values
            RebuildInMemoryConfiguration();
            
            _logger.LogInformation(
                "Settings encryption processing complete. Encrypted sections: {SectionCount}, Cached decrypted values: {ValueCount}",
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
    /// Rebuilds the in-memory IConfiguration from the decrypted values dictionary.
    /// </summary>
    private void RebuildInMemoryConfiguration()
    {
        _decryptedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(_decryptedValues)
            .Build();
        
        _logger.LogDebug("Rebuilt in-memory configuration with {Count} decrypted values", _decryptedValues.Count);
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
        var pathsSection = _configuration.GetSection("additional_configurations:path");
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
        get => GetValue(key);
        set
        {
            // Setting values updates the in-memory decrypted config only (not persisted)
            // This maintains IConfiguration contract but changes won't survive restart
            if (!string.IsNullOrWhiteSpace(key))
            {
                _decryptedValues[key] = value;
                RebuildInMemoryConfiguration();
            }
        }
    }

    /// <summary>
    /// Gets the immediate descendant configuration sub-sections.
    /// Combines children from both decrypted and main configuration.
    /// </summary>
    public IEnumerable<IConfigurationSection> GetChildren()
    {
        // Get children from decrypted configuration
        var decryptedChildren = _decryptedConfiguration.GetChildren();
        var decryptedKeys = decryptedChildren.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        // Get children from main configuration that aren't in decrypted
        var mainChildren = _configuration.GetChildren()
            .Where(c => !decryptedKeys.Contains(c.Key));
        
        // Return decrypted children first, then main children
        return decryptedChildren.Concat(mainChildren);
    }

    /// <summary>
    /// Returns a change token that can be used to observe when this configuration is reloaded.
    /// </summary>
    public IChangeToken GetReloadToken()
    {
        // Return the main configuration's reload token since that's what triggers our reload
        return _configuration.GetReloadToken();
    }

    #endregion

    #region Public API

    /// <summary>
    /// Gets a configuration value, returning the decrypted value if available.
    /// Falls back to the main IConfiguration if not found in decrypted values.
    /// </summary>
    /// <param name="key">The configuration key (e.g., "ConnectionStrings:default")</param>
    /// <returns>The decrypted value if encrypted, otherwise the raw configuration value</returns>
    public string? GetValue(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        
        // Check decrypted configuration first
        var decryptedValue = _decryptedConfiguration.GetValue<string>(key);
        if (decryptedValue != null)
        {
            return decryptedValue;
        }
        
        // Fall back to main IConfiguration
        return _configuration.GetValue<string>(key);
    }

    /// <summary>
    /// Gets a configuration value as the specified type, returning the decrypted value if available.
    /// </summary>
    /// <typeparam name="T">The type to convert the value to</typeparam>
    /// <param name="key">The configuration key</param>
    /// <returns>The converted value, or default(T) if not found</returns>
    public T? GetValue<T>(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return default;
        
        // Check decrypted configuration first
        var decryptedValue = _decryptedConfiguration.GetValue<string>(key);
        if (decryptedValue != null)
        {
            try
            {
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)decryptedValue;
                }
                return (T)Convert.ChangeType(decryptedValue, typeof(T));
            }
            catch
            {
                return default;
            }
        }
        
        // Fall back to IConfiguration
        return _configuration.GetValue<T>(key);
    }

    /// <summary>
    /// Gets a configuration section that merges decrypted values with the main configuration.
    /// This allows you to use familiar IConfigurationSection patterns with decrypted data
    /// while still having access to non-encrypted siblings.
    /// 
    /// The returned section is built by combining:
    /// 1. All decrypted values under the requested path
    /// 2. All main config values under the requested path (that aren't overridden by decrypted values)
    /// </summary>
    /// <param name="key">The section key (e.g., "ConnectionStrings" or "api_keys_collections")</param>
    /// <returns>An IConfigurationSection containing merged decrypted and main config values</returns>
    public IConfigurationSection GetSection(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return _configuration.GetSection(key);
        
        var sectionFromDecrypted = _decryptedConfiguration.GetSection(key);
        var sectionFromMain = _configuration.GetSection(key);
        
        var decryptedExists = sectionFromDecrypted.Exists() || sectionFromDecrypted.GetChildren().Any();
        var mainExists = sectionFromMain.Exists() || sectionFromMain.GetChildren().Any();
        
        // If both have values, we need to merge them
        if (decryptedExists && mainExists)
        {
            // Build a merged configuration
            var mergedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var keyPrefix = key + ":";
            
            // First, add all values from main config under this section
            foreach (var kvp in GetAllValuesUnderSection(_configuration, key))
            {
                mergedValues[kvp.Key] = kvp.Value;
            }
            
            // Then, overlay with decrypted values (these take precedence)
            foreach (var kvp in _decryptedValues)
            {
                if (kvp.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    mergedValues[kvp.Key] = kvp.Value;
                }
            }
            
            // Build a new in-memory configuration with merged values
            var mergedConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(mergedValues)
                .Build();
            
            return mergedConfig.GetSection(key);
        }
        
        if (decryptedExists)
        {
            return sectionFromDecrypted;
        }
        
        // Fall back to main IConfiguration
        return sectionFromMain;
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
        return GetValue($"ConnectionStrings:{name}");
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
    /// Gets the in-memory IConfiguration containing only decrypted values.
    /// Use this if you need direct access to the decrypted configuration.
    /// </summary>
    public IConfiguration DecryptedConfiguration => _decryptedConfiguration;

    /// <summary>
    /// Whether the encryption service is active (running on Windows).
    /// </summary>
    public bool IsActive => _isActive;

    #endregion
}
