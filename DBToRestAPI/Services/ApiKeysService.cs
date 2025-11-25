using Com.H.Threading;
using Microsoft.Extensions.Primitives;

namespace DBToRestAPI.Services;

/// <summary>
/// Service that manages API key collections from configuration with automatic reload on changes.
/// 
/// This service maintains a dictionary of API key collections where:
/// - Key: Collection name (e.g., "external_vendors", "internal_solutions")
/// - Value: HashSet of API keys belonging to that collection
/// 
/// The service automatically reloads when the "api_keys_collections" configuration section changes.
/// 
/// Thread-safe reloading is ensured using an AtomicGate to prevent concurrent reload operations.
/// 
/// Example configuration structure (api_keys.xml):
/// <settings>
///   <api_keys_collections>
///     <external_vendors>
///       <key>api key 1</key>
///       <key>api key 2</key>
///     </external_vendors>
///     <internal_solutions>
///       <key>api key 3</key>
///     </internal_solutions>
///   </api_keys_collections>
/// </settings>
/// </summary>
public class ApiKeysService
{
    private Dictionary<string, HashSet<string>> _apiKeysCollections = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiKeysService> _logger;
    private readonly AtomicGate _reloadingGate = new();

    public ApiKeysService(
        IConfiguration configuration,
        ILogger<ApiKeysService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        LoadApiKeys(); // initial load
        ChangeToken.OnChange(
            () => _configuration.GetSection("api_keys_collections").GetReloadToken(),
            LoadApiKeys);
    }

    private void LoadApiKeys()
    {
        try
        {
            if (!_reloadingGate.TryOpen()) return;

            var apiKeysSection = _configuration.GetSection("api_keys_collections");
            if (apiKeysSection == null || !apiKeysSection.Exists())
            {
                _logger.LogWarning("API keys collections configuration section not found");
                _apiKeysCollections = new();
                return;
            }

            _logger.LogDebug("=== Starting API Keys Load ===");
            _logger.LogDebug("API keys section exists: {Exists}, HasValue: {HasValue}, Value: '{Value}'", 
                apiKeysSection.Exists(), 
                !string.IsNullOrEmpty(apiKeysSection.Value),
                apiKeysSection.Value ?? "(null)");

            var allChildren = apiKeysSection.GetChildren().ToList();
            _logger.LogDebug("Total top-level children in api_keys_collections: {Count}", allChildren.Count);
            
            foreach (var child in allChildren)
            {
                _logger.LogDebug("Top-level child - Key: '{Key}', Value: '{Value}', Path: '{Path}'", 
                    child.Key, 
                    child.Value ?? "(null)", 
                    child.Path);
            }

            var newCollections = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var collectionSection in apiKeysSection.GetChildren())
            {
                var collectionName = collectionSection.Key;
                _logger.LogDebug("Processing collection: '{CollectionName}'", collectionName);
                
                var apiKeys = new HashSet<string>(StringComparer.Ordinal); // API keys are case-sensitive

                // Handle both single key (direct value) and multiple keys (children) scenarios
                // When there's only one <key>, it might be stored as a direct value
                // When there are multiple <key> elements, they're stored as children
                
                var children = collectionSection.GetChildren().ToList();
                _logger.LogDebug("Collection '{CollectionName}' has {ChildCount} children", collectionName, children.Count);
                
                if (children.Any())
                {
                    // Multiple keys scenario - iterate through children
                    foreach (var child in children)
                    {
                        _logger.LogDebug("  Child Key: '{Key}', Value: '{Value}', Path: '{Path}'", 
                            child.Key, 
                            child.Value ?? "(null)", 
                            child.Path);
                        
                        // Check if this child has its own children (happens with multiple <key> elements)
                        var grandChildren = child.GetChildren().ToList();
                        if (grandChildren.Any())
                        {
                            _logger.LogDebug("    Child '{Key}' has {GrandChildCount} grandchildren", child.Key, grandChildren.Count);
                            foreach (var grandChild in grandChildren)
                            {
                                _logger.LogDebug("      GrandChild Key: '{Key}', Value: '{Value}'", 
                                    grandChild.Key, 
                                    grandChild.Value ?? "(null)");
                                    
                                if (!string.IsNullOrWhiteSpace(grandChild.Value))
                                {
                                    apiKeys.Add(grandChild.Value);
                                    _logger.LogDebug("      Added API key from grandchild: '{ApiKey}'", grandChild.Value);
                                }
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(child.Value))
                        {
                            apiKeys.Add(child.Value);
                            _logger.LogDebug("  Added API key: '{ApiKey}'", child.Value);
                        }
                        else
                        {
                            _logger.LogDebug("  Skipped null/whitespace value for child key: '{Key}'", child.Key);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(collectionSection.Value))
                {
                    // Single key scenario - the value is directly on the section
                    _logger.LogDebug("  Single value detected: '{Value}'", collectionSection.Value);
                    apiKeys.Add(collectionSection.Value);
                }
                else
                {
                    _logger.LogDebug("  No children and no direct value for collection '{CollectionName}'", collectionName);
                }

                if (apiKeys.Count > 0)
                {
                    newCollections[collectionName] = apiKeys;
                    _logger.LogDebug(
                        "Loaded API key collection '{CollectionName}' with {KeyCount} key(s)",
                        collectionName,
                        apiKeys.Count);
                }
                else
                {
                    _logger.LogWarning(
                        "API key collection '{CollectionName}' is empty or contains only whitespace keys",
                        collectionName);
                }
            }

            _apiKeysCollections = newCollections;
            _logger.LogInformation(
                "API keys loaded successfully. Total collections: {CollectionCount}, Total keys: {KeyCount}",
                newCollections.Count,
                newCollections.Values.Sum(keys => keys.Count));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading API keys collections from configuration");
            _apiKeysCollections = new();
        }
        finally
        {
            _reloadingGate.TryClose();
        }
    }

    /// <summary>
    /// Checks if an API key exists in a specific collection.
    /// </summary>
    /// <param name="collectionName">The name of the collection to check</param>
    /// <param name="apiKey">The API key to validate</param>
    /// <returns>True if the API key exists in the specified collection, false otherwise</returns>
    public bool IsValidApiKey(string collectionName, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(collectionName) || string.IsNullOrWhiteSpace(apiKey))
            return false;

        return _apiKeysCollections.TryGetValue(collectionName, out var keys) 
            && keys.Contains(apiKey);
    }

    /// <summary>
    /// Checks if an API key exists in any of the configured collections.
    /// </summary>
    /// <param name="apiKey">The API key to validate</param>
    /// <returns>True if the API key exists in any collection, false otherwise</returns>
    public bool IsValidApiKeyInAnyCollection(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        return _apiKeysCollections.Values.Any(keys => keys.Contains(apiKey));
    }

    /// <summary>
    /// Checks if an API key exists in any of the specified collections.
    /// </summary>
    /// <param name="collectionNames">Collection names to check</param>
    /// <param name="apiKey">The API key to validate</param>
    /// <returns>True if the API key exists in any of the specified collections, false otherwise</returns>
    public bool IsValidApiKeyInCollections(IEnumerable<string> collectionNames, string apiKey)
    {
        if (collectionNames == null || string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("IsValidApiKeyInCollections called with null/empty parameters");
            return false;
        }

        _logger.LogDebug("Validating API key against collections: {Collections}", 
            string.Join(", ", collectionNames.Select(c => $"'{c}'")));

        foreach (var collectionName in collectionNames)
        {
            if (_apiKeysCollections.TryGetValue(collectionName, out var keys))
            {
                _logger.LogDebug("  Collection '{CollectionName}' found with {KeyCount} keys", 
                    collectionName, 
                    keys.Count);
                    
                if (keys.Contains(apiKey))
                {
                    _logger.LogDebug("  API key validated successfully in collection '{CollectionName}'", collectionName);
                    return true;
                }
                else
                {
                    _logger.LogDebug("  API key not found in collection '{CollectionName}'", collectionName);
                }
            }
            else
            {
                _logger.LogWarning("  Collection '{CollectionName}' not found. Available collections: {AvailableCollections}", 
                    collectionName,
                    string.Join(", ", _apiKeysCollections.Keys.Select(k => $"'{k}'")));
            }
        }

        _logger.LogDebug("API key validation failed - not found in any of the specified collections");
        return false;
    }

    /// <summary>
    /// Gets all configured collection names.
    /// </summary>
    /// <returns>A read-only collection of collection names</returns>
    public IReadOnlyCollection<string> GetCollectionNames()
    {
        return _apiKeysCollections.Keys.ToList().AsReadOnly();
    }

    /// <summary>
    /// Checks if a collection exists.
    /// </summary>
    /// <param name="collectionName">The name of the collection to check</param>
    /// <returns>True if the collection exists, false otherwise</returns>
    public bool CollectionExists(string collectionName)
    {
        if (string.IsNullOrWhiteSpace(collectionName))
            return false;

        return _apiKeysCollections.ContainsKey(collectionName);
    }

    /// <summary>
    /// Gets the count of API keys in a specific collection.
    /// </summary>
    /// <param name="collectionName">The name of the collection</param>
    /// <returns>The number of API keys in the collection, or 0 if the collection doesn't exist</returns>
    public int GetCollectionKeyCount(string collectionName)
    {
        if (string.IsNullOrWhiteSpace(collectionName))
            return 0;

        return _apiKeysCollections.TryGetValue(collectionName, out var keys) 
            ? keys.Count 
            : 0;
    }
}
