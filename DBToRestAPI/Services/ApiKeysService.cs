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

            var newCollections = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var collectionSection in apiKeysSection.GetChildren())
            {
                var collectionName = collectionSection.Key;
                var apiKeys = collectionSection.GetChildren()
                    .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                    .Select(x => x.Value!)
                    .ToHashSet(StringComparer.Ordinal); // API keys are case-sensitive

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
            return false;

        foreach (var collectionName in collectionNames)
        {
            if (_apiKeysCollections.TryGetValue(collectionName, out var keys) 
                && keys.Contains(apiKey))
            {
                return true;
            }
        }

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
