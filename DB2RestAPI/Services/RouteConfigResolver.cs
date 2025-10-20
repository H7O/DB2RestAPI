using Com.H.Threading;
using Microsoft.Extensions.Primitives;

/// <summary>
/// Resolves API gateway routes from configuration by matching incoming route paths
/// against both exact routes and wildcard routes.
/// 
/// This service maintains two collections:
/// - Exact routes: Dictionary for O(1) lookup of routes with exact path matches
/// - Wildcard routes: List of routes ending with /* that match path prefixes
/// 
/// The resolver automatically reloads routes when the configuration changes.
/// 
/// Route matching priority:
/// 1. First attempts exact match lookup (fastest)
/// 2. Then checks wildcard routes, ordered by prefix length (most specific first)
/// 
/// Example route configurations:
/// - Exact route: "api/users" matches only "api/users"
/// - Wildcard route: "api/users/*" matches "api/users/123", "api/users/profile", etc.
/// 
/// Thread-safe reloading is ensured using an AtomicGate to prevent concurrent reload operations.
/// </summary>
public class RouteConfigResolver
{
    private Dictionary<string, IConfigurationSection> _exactRoutes = new();
    private List<(string Prefix, IConfigurationSection Config)> _wildcardRoutes = new();
    private readonly IConfiguration _configuration;

    private readonly AtomicGate _reloadingGate = new();
    
    public RouteConfigResolver(IConfiguration configuration)
    {
        _configuration = configuration;
        LoadRoutes(); // initial load
        ChangeToken.OnChange(
            () => _configuration.GetSection("routes").GetReloadToken(), 
            LoadRoutes);
    }

    private void LoadRoutes()
    {
        try
        {
            if (!_reloadingGate.TryOpen()) return;
            var routesSection = _configuration.GetSection("routes");
            if (routesSection == null || !routesSection.Exists())
                return;

            var newExactRoutes = new Dictionary<string, IConfigurationSection>();
            var newWildcardRoutes = new List<(string Prefix, IConfigurationSection Config)>();

            foreach (var routeSection in routesSection.GetChildren())
            {
                var route = routeSection.GetValue<string>("route") ?? routeSection.Key;

                if (route.EndsWith("/*"))
                {
                    // Store wildcard routes separately with their prefix (without the /*)

                    var prefix = route[..^2];
                    // the above is just a shortcut for the below
                    // var prefix = path.Substring(0, path.Length - 2); // removes the /*
                    newWildcardRoutes.Add((prefix, routeSection));
                }
                else
                {
                    // Exact routes can use dictionary for O(1) lookup
                    newExactRoutes[route] = routeSection;
                }
            }

            // Sort wildcard routes by descending prefix length for most specific matching
            newWildcardRoutes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));

            _exactRoutes = newExactRoutes;
            _wildcardRoutes = newWildcardRoutes;
        }
        finally
        {
            _reloadingGate.TryClose();
        }

    }

    /// <summary>
    /// Resolves the configuration section for a given route path.
    /// Matches exact routes first, then wildcard routes by prefix.
    /// The most specific wildcard match is returned if multiple matches are found.
    /// Returns null if no match is found.
    /// </summary>
    /// <param name="route"></param>
    /// <returns></returns>

    public IConfigurationSection? ResolveRoute(string route)
    {
        // First, try exact match (O(1) lookup)
        if (_exactRoutes.TryGetValue(route, out var exactMatch))
        {
            return exactMatch;
        }

        // If no exact match, check wildcard routes (most specific first)
        foreach (var (prefix, config) in _wildcardRoutes)
        {
            if (route.StartsWith(prefix + "/"))
            {
                return config;
            }
        }

        return null; // No matching route found
    }
    
    /// <summary>
    /// Gets the remaining path after the matched route prefix.
    /// For example, if the route is "api/users/123" and the matched config
    /// is "api/users/*", this method would return "123".
    /// </summary>
    /// <param name="route"></param>
    /// <param name="matchedConfig"></param>
    /// <returns></returns>
    public string GetRemainingPath(string route, IConfigurationSection matchedConfig)
    {
        var configPath = matchedConfig.GetValue<string>("route") ?? matchedConfig.Key;
        
        if (configPath.EndsWith("/*"))
        {
            var prefix = configPath[..^2];
            // the above is just a shortcut for the below
            // var prefix = configPath.Substring(0, configPath.Length - 2);
            if (route.StartsWith(prefix + "/"))
            {
                return route[(prefix.Length + 1)..];
                // the above is just a shortcut for the below
                // return route.Substring(prefix.Length + 1);
            }
        }
        
        return string.Empty;
    }
}