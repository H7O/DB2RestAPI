using DBToRestAPI.Services;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace DBToRestAPI.Middlewares;

/// <summary>
/// Middleware that handles Cross-Origin Resource Sharing (CORS) headers.
/// 
/// This middleware:
/// - Checks if the request has an Origin header
/// - Looks for route-specific CORS configuration (highest priority)
/// - Falls back to global CORS configuration from settings.xml
/// - Applies CORS headers based on regex pattern matching
/// - Handles preflight OPTIONS requests
/// - Defaults to allowing all origins (*) if no configuration is found
/// 
/// Required context.Items from previous middlewares:
/// - `section`: IConfigurationSection for the route's configuration (non-OPTIONS requests)
/// - `sections`: List&lt;IConfigurationSection&gt; for all matching routes (OPTIONS preflight requests)
/// 
/// Sets CORS headers:
/// - Access-Control-Allow-Origin
/// - Access-Control-Allow-Methods
/// - Access-Control-Allow-Headers
/// - Access-Control-Allow-Credentials
/// - Access-Control-Max-Age
/// </summary>
public class Step2CorsCheck(
    RequestDelegate next,
    IEncryptedConfiguration settingsEncryptionService,
    ILogger<Step2CorsCheck> logger)
{
    private readonly RequestDelegate _next = next;
    // private readonly IConfiguration _configuration = configuration;
    private readonly IEncryptedConfiguration _configuration = settingsEncryptionService;
    private readonly ILogger<Step2CorsCheck> _logger = logger;
    private static readonly string _errorCode = "Step 2 - CORS Check Error";
    private static readonly string _defaultMethods = "GET, POST, PUT, DELETE, PATCH, OPTIONS";
    private static readonly string _defaultHeaders = "Authorization, Content-Type, X-Requested-With, Accept, Origin, X-Api-Key";
    private const long DefaultMaxAge = 86400; // 1 day


    public async Task InvokeAsync(HttpContext context)
    {
        #region log the time and the middleware name
        this._logger.LogDebug("{time}: in Step2_CorsCheck middleware",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
        #endregion


        // Handle OPTIONS preflight requests - use multiple sections to aggregate allowed methods
        if (context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            List<IConfigurationSection>? sections = context.Items.TryGetValue("sections", out var sectionsValue)
                ? sectionsValue as List<IConfigurationSection>
                : null;

            if (sections == null || sections.Count == 0)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }

            // Apply CORS headers using all matching sections
            ApplyCorsHeaders(context, sections);
            context.Response.StatusCode = 204; // No Content
            return; // Short-circuit the pipeline
        }

        // Non-OPTIONS requests use single section
        #region if no section passed from the previous middlewares, return 500
        IConfigurationSection? section = context.Items.TryGetValue("section", out var sectionValue)
            ? sectionValue as IConfigurationSection
            : null;

        if (section == null)
        {
            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(
                    new
                    {
                        success = false,
                        message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    }
                )
                {
                    StatusCode = 500
                }
            );
            return;
        }
        #endregion

        // Apply CORS headers using single section
        ApplyCorsHeaders(context, section);

        // Proceed to the next middleware
        await _next(context);
    }

    #region ApplyCorsHeaders

    /// <summary>
    /// Applies CORS headers for a single section (non-OPTIONS requests).
    /// </summary>
    private void ApplyCorsHeaders(HttpContext context, IConfigurationSection section)
    {
        var origin = context.Request.Headers["Origin"].ToString();

        string allowedOrigin = DetermineAllowedOrigin(section, origin);
        string allowedMethods = GetAllowedMethods(section);
        string? allowedHeaders = GetAllowedHeaders(section);
        string allowCredentials = GetAllowCredentials(section);
        long maxAge = GetMaxAge(section);

        SetCorsResponseHeaders(context, allowedOrigin, allowedMethods, allowedHeaders, allowCredentials, maxAge);
    }

    /// <summary>
    /// Applies CORS headers for multiple sections (OPTIONS preflight requests).
    /// Aggregates allowed methods from all matching route sections.
    /// </summary>
    private void ApplyCorsHeaders(HttpContext context, List<IConfigurationSection> sections)
    {
        var origin = context.Request.Headers["Origin"].ToString();

        string allowedOrigin = DetermineAllowedOrigin(sections, origin);
        string allowedMethods = GetAllowedMethods(sections);
        string? allowedHeaders = GetAllowedHeaders(sections);
        string allowCredentials = GetAllowCredentials(sections);
        long maxAge = GetMaxAge(sections);

        SetCorsResponseHeaders(context, allowedOrigin, allowedMethods, allowedHeaders, allowCredentials, maxAge);
    }

    /// <summary>
    /// Sets the actual CORS response headers.
    /// </summary>
    private void SetCorsResponseHeaders(
        HttpContext context,
        string allowedOrigin,
        string allowedMethods,
        string? allowedHeaders,
        string allowCredentials,
        long maxAge)
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = allowedOrigin;
        context.Response.Headers["Access-Control-Allow-Methods"] = allowedMethods;

        if (allowCredentials.Equals("true"))
            context.Response.Headers["Access-Control-Allow-Credentials"] = allowCredentials;

        context.Response.Headers["Access-Control-Allow-Headers"] = allowedHeaders ?? (allowCredentials == "true" ? _defaultHeaders : "*");
        context.Response.Headers["Access-Control-Max-Age"] = maxAge.ToString();

        this._logger.LogDebug("CORS headers set: Origin={origin}, Methods={methods}, MaxAge={maxAge}",
            allowedOrigin, allowedMethods, maxAge);
    }

    #endregion

    #region origin determination
    private string DetermineAllowedOrigin(IConfigurationSection section, string? origin)
    {
        // Get CORS settings with fallback chain: route-specific > global
        var pattern = GetCorsValue(section, "pattern");
        var fallbackOrigin = GetCorsValue(section, "fallback_origin");

        // Normalize fallback origin (add https:// if missing) or default to "*"
        var normalizedFallback = string.IsNullOrWhiteSpace(fallbackOrigin)
            ? "*"
            : NormalizeFallbackOrigin(fallbackOrigin);

        // Case 1: Browser request with Origin header
        if (!string.IsNullOrWhiteSpace(origin))
        {
            var matchedOrigin = DetermineOriginForBrowserRequest(origin, pattern);

            // If pattern matched, return the origin; otherwise use fallback
            return matchedOrigin ?? normalizedFallback;
        }

        // Case 2: Non-browser request (no Origin header) - use fallback (or "*")
        if (normalizedFallback != "*")
        {
            this._logger.LogDebug("CORS: No Origin header, using fallback_origin '{fallback}' for non-browser request",
                normalizedFallback);
        }
        else
        {
            this._logger.LogDebug("CORS: No Origin header and no fallback_origin configured, using '*' for non-browser request");
        }

        return normalizedFallback;
    }

    private string? DetermineOriginForBrowserRequest(string origin, string? pattern)
    {
        // No pattern configured - return null (will use fallback)
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        // Pattern configured - check if origin matches
        try
        {
            var originDomain = new Uri(origin).Host;

            if (Regex.IsMatch(originDomain, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                // Origin matches pattern - allow it
                this._logger.LogDebug("CORS: Origin '{origin}' matches pattern '{pattern}'", origin, pattern);
                return origin;
            }

            // Origin doesn't match pattern - return null (will use fallback)
            this._logger.LogDebug("CORS: Origin '{origin}' doesn't match pattern '{pattern}'", origin, pattern);
            return null;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "CORS: Error processing origin '{origin}' with pattern '{pattern}'", origin, pattern);
            // On error, return null (will use fallback)
            return null;
        }
    }

    private static string NormalizeFallbackOrigin(string fallbackOrigin)
    {
        return fallbackOrigin.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? fallbackOrigin
            : $"https://{fallbackOrigin}";
    }

    /// <summary>
    /// Determines allowed origin by checking ALL sections for CORS configuration.
    /// Used for OPTIONS preflight - uses first section with a matching pattern, or first fallback.
    /// </summary>
    private string DetermineAllowedOrigin(List<IConfigurationSection> sections, string? origin)
    {
        // For browser requests with Origin header, try to find a matching pattern in any section
        if (!string.IsNullOrWhiteSpace(origin))
        {
            foreach (var section in sections)
            {
                var pattern = GetCorsValue(section, "pattern");
                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    var matchedOrigin = DetermineOriginForBrowserRequest(origin, pattern);
                    if (matchedOrigin != null)
                    {
                        this._logger.LogDebug("CORS: Origin matched pattern in one of {count} sections", sections.Count);
                        return matchedOrigin;
                    }
                }
            }
        }

        // No pattern matched - find the first fallback_origin configured, or use "*"
        foreach (var section in sections)
        {
            var fallbackOrigin = GetCorsValue(section, "fallback_origin");
            if (!string.IsNullOrWhiteSpace(fallbackOrigin))
            {
                var normalized = NormalizeFallbackOrigin(fallbackOrigin);
                this._logger.LogDebug("CORS: Using fallback_origin '{fallback}' from one of {count} sections", normalized, sections.Count);
                return normalized;
            }
        }

        // No fallback configured in any section, use "*"
        this._logger.LogDebug("CORS: No pattern match or fallback_origin in any of {count} sections, using '*'", sections.Count);
        return "*";
    }

    #endregion

    #region allowed headers determination

    /// <summary>
    /// Gets allowed headers from a single section.
    /// </summary>
    private string? GetAllowedHeaders(IConfigurationSection section)
    {
        var headers = GetCorsValue(section, "allowed_headers");

        if (string.IsNullOrWhiteSpace(headers))
        {
            this._logger.LogDebug("CORS: Using default allowed headers");
            return null; // Will be replaced with _defaultHeaders or "*" in SetCorsResponseHeaders
        }

        return headers;
    }

    /// <summary>
    /// Gets allowed headers by checking ALL sections.
    /// Uses the first section that has allowed_headers configured.
    /// </summary>
    private string? GetAllowedHeaders(List<IConfigurationSection> sections)
    {
        foreach (var section in sections)
        {
            var headers = GetCorsValue(section, "allowed_headers");
            if (!string.IsNullOrWhiteSpace(headers))
            {
                this._logger.LogDebug("CORS: Using allowed_headers from one of {count} sections", sections.Count);
                return headers;
            }
        }

        this._logger.LogDebug("CORS: No allowed_headers in any of {count} sections, using default", sections.Count);
        return null;
    }

    #endregion


    #region allowed methods

    /// <summary>
    /// Gets the allowed HTTP methods for CORS based on the route's verb configuration.
    /// If verb is defined in the route config, uses those methods (uppercased).
    /// Otherwise, defaults to allowing all common methods plus OPTIONS.
    /// </summary>
    private string GetAllowedMethods(IConfigurationSection section)
    {
        var verb = section.GetValue<string>("verb");
        if (string.IsNullOrWhiteSpace(verb))
        {
            verb = _configuration.GetValue<string>("verb");
        }

        if (string.IsNullOrWhiteSpace(verb))
        {
            this._logger.LogDebug("CORS: No verb configuration found, using default allowed methods");
            return _defaultMethods;
        }

        var verbs = verb.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.ToUpperInvariant())
            .ToHashSet();

        verbs.Add("OPTIONS");

        var methods = string.Join(", ", verbs.OrderBy(v => v));
        this._logger.LogDebug("CORS: Using route-specific allowed methods: {methods}", methods);
        return methods;
    }

    /// <summary>
    /// Gets the allowed HTTP methods by aggregating verbs from ALL matching route sections.
    /// Used for OPTIONS preflight requests to report all methods available on the endpoint.
    /// </summary>
    private string GetAllowedMethods(List<IConfigurationSection> sections)
    {
        var allVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            var verb = section.GetValue<string>("verb");
            if (!string.IsNullOrWhiteSpace(verb))
            {
                var verbs = verb.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var v in verbs)
                {
                    allVerbs.Add(v.ToUpperInvariant());
                }
            }
        }

        // If no verbs found in any section, check global config
        if (allVerbs.Count == 0)
        {
            var globalVerb = _configuration.GetValue<string>("verb");
            if (!string.IsNullOrWhiteSpace(globalVerb))
            {
                var verbs = globalVerb.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var v in verbs)
                {
                    allVerbs.Add(v.ToUpperInvariant());
                }
            }
        }

        // If still no verbs, use defaults
        if (allVerbs.Count == 0)
        {
            this._logger.LogDebug("CORS: No verb configuration found in any section, using default allowed methods");
            return _defaultMethods;
        }

        allVerbs.Add("OPTIONS");

        var methods = string.Join(", ", allVerbs.OrderBy(v => v));
        this._logger.LogDebug("CORS: Aggregated allowed methods from {count} sections: {methods}", sections.Count, methods);
        return methods;
    }

    #endregion

    #region allow credentials

    /// <summary>
    /// Gets the allow credentials value for a single section.
    /// Checks cors:allow_credentials config, then falls back to checking authorize section.
    /// </summary>
    private string GetAllowCredentials(IConfigurationSection section)
    {
        var allowCredentials = GetCorsValue(section, "allow_credentials");

        if (!string.IsNullOrWhiteSpace(allowCredentials)
            && bool.TryParse(allowCredentials, out var allowCredentialsBool))
        {
            return allowCredentialsBool.ToString().ToLowerInvariant();
        }

        // Check if there is an `authorize` section in the route or global config
        if (section.GetSection("authorize").Exists() || _configuration.GetSection("authorize").Exists())
            return "true";

        return "false";
    }

    /// <summary>
    /// Gets the allow credentials value by checking ALL sections.
    /// Returns "true" if ANY section has explicit allow_credentials=true OR has an authorize section.
    /// </summary>
    private string GetAllowCredentials(List<IConfigurationSection> sections)
    {
        // Check global config for authorize first
        if (_configuration.GetSection("authorize").Exists())
            return "true";

        foreach (var section in sections)
        {
            // Check explicit allow_credentials setting
            var allowCredentials = GetCorsValue(section, "allow_credentials");
            if (!string.IsNullOrWhiteSpace(allowCredentials)
                && bool.TryParse(allowCredentials, out var allowCredentialsBool)
                && allowCredentialsBool)
            {
                return "true";
            }

            // Check for authorize section
            if (section.GetSection("authorize").Exists())
                return "true";
        }

        return "false";
    }

    #endregion

    #region max age

    /// <summary>
    /// Gets the max_age value from a single section.
    /// </summary>
    private long GetMaxAge(IConfigurationSection section)
    {
        string? maxAge = GetCorsValue(section, "max_age");
        if (!string.IsNullOrWhiteSpace(maxAge) && long.TryParse(maxAge, out var maxAgeValue) && maxAgeValue >= 0)
        {
            return maxAgeValue;
        }
        return DefaultMaxAge;
    }

    /// <summary>
    /// Gets the shortest max_age value from ALL sections.
    /// Uses the shortest to ensure the browser re-checks sooner if any route has a shorter cache time.
    /// </summary>
    private long GetMaxAge(List<IConfigurationSection> sections)
    {
        long shortestMaxAge = DefaultMaxAge;
        bool foundAny = false;

        foreach (var section in sections)
        {
            string? maxAge = GetCorsValue(section, "max_age");
            if (!string.IsNullOrWhiteSpace(maxAge) && long.TryParse(maxAge, out var maxAgeValue) && maxAgeValue >= 0)
            {
                if (!foundAny || maxAgeValue < shortestMaxAge)
                {
                    shortestMaxAge = maxAgeValue;
                    foundAny = true;
                }
            }
        }

        if (foundAny)
        {
            this._logger.LogDebug("CORS: Using shortest max_age {maxAge} from {count} sections", shortestMaxAge, sections.Count);
        }

        return shortestMaxAge;
    }

    #endregion

    /// <summary>
    /// Gets a CORS configuration value with fallback chain: route-specific cors > global cors
    /// </summary>
    private string? GetCorsValue(IConfigurationSection section, string key)
    {
        var value = section.GetValue<string>($"cors:{key}");
        if (string.IsNullOrWhiteSpace(value))
        {
            value = _configuration.GetValue<string>($"cors:{key}");
        }

        return value;
    }



}

