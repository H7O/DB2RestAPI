using Com.H.Threading;
using Microsoft.Extensions.Primitives;

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
        if (!_reloadingGate.TryOpen()) return;
        var routesSection = _configuration.GetSection("routes");
		if (routesSection == null || !routesSection.Exists())
			return;

        var newExactRoutes = new Dictionary<string, IConfigurationSection>();
        var newWildcardRoutes = new List<(string Prefix, IConfigurationSection Config)>();
		
        foreach (var routeSection in routesSection.GetChildren())
        {
            var path = routeSection.GetValue<string>("path") ?? routeSection.Key;
            
            if (path.EndsWith("/*"))
            {
                // Store wildcard routes separately with their prefix (without the /*)
                
                var prefix = path[..^2];
                // the above is just a shortcut for the below
                // var prefix = path.Substring(0, path.Length - 2);
                newWildcardRoutes.Add((prefix, routeSection));
            }
            else
            {
                // Exact routes can use dictionary for O(1) lookup
                newExactRoutes[path] = routeSection;
            }
        }
        
        // Sort wildcard routes by descending prefix length for most specific matching
        newWildcardRoutes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));

        _exactRoutes = newExactRoutes;
        _wildcardRoutes = newWildcardRoutes;

        _reloadingGate.TryClose();
    }
    
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
    
    public string GetRemainingPath(string route, IConfigurationSection matchedConfig)
    {
        var configPath = matchedConfig.GetValue<string>("path") ?? matchedConfig.Key;
        
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