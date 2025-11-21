using Com.H.Threading;
using DB2RestAPI.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using System.Text.RegularExpressions;

/// <summary>
/// Resolves database query routes from configuration by matching incoming route paths and HTTP verbs
/// against configured query endpoints, including support for parameterized routes.
/// 
/// This service maintains two collections for optimal performance:
/// - Exact routes: Routes without variables for fast direct lookup
/// - Parameterized routes: Routes with variables (e.g., {id}, {name}) that require pattern matching
/// 
/// The resolver automatically reloads routes when the configuration changes.
/// 
/// Route matching process:
/// 1. Attempts exact match with both route and verb
/// 2. Falls back to exact route match with no verb specified (verb-agnostic)
/// 3. Uses scoring algorithm to find best matching parameterized route
/// 
/// Scoring algorithm prioritizes specificity:
/// - Exact segment matches score higher (10 points) than parameter matches (5 points)
/// - Routes must have matching segment counts to be considered
/// - The route with the highest score wins
/// 
/// Example route configurations:
/// - Exact route: "api/users" with verb "GET" matches only exact path with GET method
/// - Parameterized route: "api/users/{id}" matches "api/users/123", "api/users/abc", etc.
/// - Verb-agnostic route: "api/data" with no verb matches any HTTP method
/// 
/// Thread-safe reloading is ensured using an AtomicGate to prevent concurrent reload operations.
/// </summary>
public class QueryRouteResolver
{

    private List<(string NormalizedRoute, HashSet<string> Verbs, IConfigurationSection Config)> _exactRoutes = new();
    private List<(string NormalizedRoute, HashSet<string> Verbs, IConfigurationSection Config)> _routesWithVariables = new();
    // private Dictionary<string, HashSet<string>> _exactRouteVerbs = new();
    private readonly IConfiguration _configuration;


    private readonly AtomicGate _reloadingGate = new();
    
    public QueryRouteResolver(IConfiguration configuration)
    {
        _configuration = configuration;
        LoadRoutes(); // initial load
        ChangeToken.OnChange(
            () => _configuration.GetSection("queries").GetReloadToken(), 
            LoadRoutes);
    }

    private static readonly HashSet<string> _allVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"
    };

    private void LoadRoutes()
    {
        try 
        {
            if (!_reloadingGate.TryOpen()) return;

            var querySections = _configuration.GetSection("queries");
            if (querySections == null || !querySections.Exists())
                return;

            var newExactRoutes = new List<(string NormalizedRoute, HashSet<string> Verbs, IConfigurationSection Config)>();
            var newRoutesWithVariables = new List<(string NormalizedRoute, HashSet<string> Verbs, IConfigurationSection Config)>();
            var newExactRouteVerbs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var querySection in querySections.GetChildren())
            {
                var route = querySection.GetValue<string>("route") ?? querySection.Key;
                if (string.IsNullOrWhiteSpace(route)) continue;
                var normalizedRoute = NormalizeRoute(route);
                if (string.IsNullOrWhiteSpace(normalizedRoute)) continue;
                
                var verbString = querySection.GetValue<string>("verb");
                var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                if (!string.IsNullOrWhiteSpace(verbString))
                {
                    var splitVerbs = verbString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var v in splitVerbs) verbs.Add(v);
                    // Ensure OPTIONS is present if specific verbs are defined
                    verbs.Add("OPTIONS");
                }
                else
                {
                    // No verb defined -> implies all verbs
                    foreach (var v in _allVerbs) verbs.Add(v);
                }

                var routeParameterPattern = 
                querySection.GetValue<string>("route_variable_pattern")
                ??_configuration.GetValue<string>("route_variable_pattern");

                var routeParametersRegex = string.IsNullOrWhiteSpace(routeParameterPattern) ? 
                    DefaultRegex.DefaultRouteVariablesCompiledRegex 
                    : new Regex(routeParameterPattern, RegexOptions.Compiled);

                if (routeParametersRegex.IsMatch(normalizedRoute))
                {
                    // Store routes with variables separately
                    newRoutesWithVariables.Add((normalizedRoute, verbs, querySection));
                }
                else
                {
                    // Store exact routes separately
                    newExactRoutes.Add((normalizedRoute, verbs, querySection));

                    if (!newExactRouteVerbs.ContainsKey(normalizedRoute))
                    {
                        newExactRouteVerbs[normalizedRoute] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    
                    foreach (var v in verbs)
                    {
                        newExactRouteVerbs[normalizedRoute].Add(v);
                    }
                }
            }
            
            _exactRoutes = newExactRoutes;
            _routesWithVariables = newRoutesWithVariables;
            // _exactRouteVerbs = newExactRouteVerbs;
        }
        finally
        {
            _reloadingGate.TryClose();
        }
    }
    

    public IConfigurationSection? ResolveRoute(string urlRoute, string verb)
    {
        if (string.IsNullOrWhiteSpace(urlRoute) || string.IsNullOrWhiteSpace(verb))
            return null;
        // Normalize inputs - remove leading/trailing slashes for consistent comparison
        // with the normalized routes in the config
        urlRoute = NormalizeRoute(urlRoute);
        
        // Try exact match with both route and verb
        var exactMatch = _exactRoutes.FirstOrDefault(rc => 
            string.Equals(rc.NormalizedRoute, urlRoute, StringComparison.OrdinalIgnoreCase) && 
            rc.Verbs.Contains(verb));
        
        if (exactMatch.Config != null)
        {
            return exactMatch.Config;
        }
        
        // If no exact match, try best matching route with variables
        return GetBestMatchingRouteConfig(urlRoute, verb);
    }

    //public IEnumerable<string> GetPossibleVerbs(string urlRoute)
    //{
    //    if (string.IsNullOrWhiteSpace(urlRoute)) return Enumerable.Empty<string>();

    //    var normalizedUrlRoute = NormalizeRoute(urlRoute);
    //    var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    //    // 1. Exact matches
    //    if (_exactRouteVerbs.TryGetValue(normalizedUrlRoute, out var exactVerbs))
    //    {
    //        foreach (var verb in exactVerbs)
    //        {
    //            verbs.Add(verb);
    //        }
    //    }

    //    // 2. Variable matches
    //    if (_routesWithVariables != null)
    //    {
    //        var urlSegments = normalizedUrlRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);

    //        foreach (var (NormalizedConfigRoute, Verbs, Config) in _routesWithVariables)
    //        {
    //            // Optimization: check segment count first
    //            var configSegments = NormalizedConfigRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);
    //            if (configSegments.Length != urlSegments.Length) continue;

    //            var routeParameterPattern = Config.GetValue<string>("route_variable_pattern")
    //               ?? _configuration.GetValue<string>("route_variable_pattern");

    //            var routeParametersRegex = string.IsNullOrWhiteSpace(routeParameterPattern) ?
    //               DefaultRegex.DefaultRouteVariablesCompiledRegex :
    //               new Regex(routeParameterPattern, RegexOptions.Compiled);

    //            if (CalculateRouteMatchScore(urlSegments, NormalizedConfigRoute, routeParametersRegex) > 0)
    //            {
    //                foreach (var v in Verbs)
    //                {
    //                    verbs.Add(v);
    //                }
    //            }
    //        }
    //    }

    //    return verbs;
    //}

    public Dictionary<string, string> GetRouteParametersIfAny(
        IConfigurationSection configSection,
        string urlRoute
        )
    {
        if (configSection == null 
            || !configSection.Exists()
            || string.IsNullOrWhiteSpace(urlRoute)) return [];
        
        var configRoute = configSection.GetValue<string>("route") ?? configSection.Key;
        if (string.IsNullOrWhiteSpace(configRoute)) return [];

        // Split both strings into segments
        string[] urlSegments = urlRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] configSegments = configRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var routeParameterPattern = configSection?.GetValue<string>("route_variable_pattern");
        if (string.IsNullOrWhiteSpace(routeParameterPattern))
            routeParameterPattern = configSection?.GetValue<string>("route_variables_pattern");
        if (string.IsNullOrWhiteSpace(routeParameterPattern))
            routeParameterPattern = DefaultRegex.DefaultRouteVariablesPattern;

        var routeParametersRegex = string.IsNullOrWhiteSpace(routeParameterPattern) ? 
            DefaultRegex.DefaultRouteVariablesCompiledRegex : 
            new Regex(routeParameterPattern, RegexOptions.Compiled);

        Dictionary<string, string> parameters = [];

        for (int i = 0; i < configSegments.Length && i < urlSegments.Length; i++)
        {
            string configSegment = configSegments[i];
            Match match = routeParametersRegex.Match(configSegment);

            if (match.Success && match.Groups["param"].Success)
            {
                string paramName = match.Groups["param"].Value;
                string paramValue = urlSegments[i];

                parameters.Add(paramName, paramValue);
            }
        }

        return parameters;
    }

    /// <summary>
    /// Returns the best matching route configuration based on the provided URL path and HTTP verb.
    /// Uses a scoring algorithm to find the most specific match, prioritizing exact matches over parameter matches.
    /// </summary>
    /// <param name="normalizedUrlRoute">The normalized URL to match against config URLs</param>
    /// <param name="verb">The HTTP verb to match alongside the urlPath in config URLs</param>
    /// <returns>The best matching route config Section (only if found, otherwise return null)</returns>
    private IConfigurationSection? GetBestMatchingRouteConfig(
        string normalizedUrlRoute,
        string verb
        )
    {
        if (string.IsNullOrWhiteSpace(normalizedUrlRoute) 
            || string.IsNullOrWhiteSpace(verb) 
            || _routesWithVariables == null)
            return null;
        
        // Filter by verb first for performance (case insensitive)
        var verbMatches = _routesWithVariables.Where(rc => 
            rc.Verbs.Contains(verb));

        if (!verbMatches.Any())
            return null;

        IConfigurationSection? bestMatch = null;
        var bestScore = -1;

        
        var urlSegments = normalizedUrlRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);


        foreach (var (NormalizedConfigRoute, Verbs, Config) in verbMatches)
        {
            // do a direct comparison first for performance
            if (string.Equals(normalizedUrlRoute, NormalizedConfigRoute, StringComparison.OrdinalIgnoreCase))
            {
                return Config;
            }

            var routeParameterPattern = Config.GetValue<string>("route_variable_pattern")
            ??_configuration.GetValue<string>("route_variable_pattern");

            var routeParametersRegex = string.IsNullOrWhiteSpace(routeParameterPattern) ? 
                DefaultRegex.DefaultRouteVariablesCompiledRegex :
                new Regex(routeParameterPattern, RegexOptions.Compiled);

            // Calculate the match score for the route config
            var score = CalculateRouteMatchScore(urlSegments, NormalizedConfigRoute, routeParametersRegex);
            
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = Config;
            }
        }

        return bestScore > 0 ? bestMatch : null;
    }

    /// <summary>
    /// Calculates a match score for a given URL path against a route configuration.
    /// Higher scores indicate better matches.
    /// </summary>
    /// <param name="urlSegments">Pre-split URL segments</param>
    /// <param name="normalizedConfigRoute">Already normalized route configuration to match against</param>
    /// <param name="parameterRegex">Compiled regex for parameter detection</param>
    /// <returns>Match score (higher is better, -1 for no match)</returns>
    private static int CalculateRouteMatchScore(string[] urlSegments, string normalizedConfigRoute, Regex parameterRegex)
    {
        var configSegments = normalizedConfigRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Early exit if segment counts don't match - exact segment count required for best match
        if (urlSegments.Length != configSegments.Length)
            return -1;

        var score = 0;
        const int EXACT_MATCH_POINTS = 10;
        const int PARAMETER_MATCH_POINTS = 5;

        for (int i = 0; i < configSegments.Length; i++)
        {
            var configSegment = configSegments[i];
            var urlSegment = urlSegments[i];

            if (string.Equals(configSegment, urlSegment, StringComparison.OrdinalIgnoreCase))
            {
                // Exact match - highest score
                score += EXACT_MATCH_POINTS;
                continue;
            }
            
            // Check if this segment is a parameter
            var match = parameterRegex.Match(configSegment);
            if (match.Success && match.Groups["param"].Success)
            {
                // Parameter segment - always matches, but lower score
                score += PARAMETER_MATCH_POINTS;
                continue;
            }

            // No match for this segment - route doesn't match
            return -1;
        }

        return score;
    }

    /// <summary>
    /// Normalizes a path by removing leading and trailing slashes and handling empty paths.
    /// </summary>
    /// <param name="route">Path to normalize</param>
    /// <returns>Normalized path</returns>
    private static string NormalizeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return string.Empty;

        return route.Trim('/');
    }

}