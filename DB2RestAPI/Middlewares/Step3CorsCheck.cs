using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace DB2RestAPI.Middlewares;

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
/// - `section`: IConfigurationSection for the route's configuration
/// 
/// Sets CORS headers:
/// - Access-Control-Allow-Origin
/// - Access-Control-Allow-Methods
/// - Access-Control-Allow-Headers
/// - Access-Control-Allow-Credentials
/// - Access-Control-Max-Age
/// </summary>
public class Step3CorsCheck(
    RequestDelegate next,
    IConfiguration configuration,
    ILogger<Step3CorsCheck> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<Step3CorsCheck> _logger = logger;
    private static readonly string _errorCode = "Step 3 - CORS Check Error";
    private static readonly string _defaultMethods = "GET, POST, PUT, DELETE, PATCH, OPTIONS";
    // private static readonly string _defaultHeaders = "Content-Type, Authorization, X-Api-Key, X-Requested-With";


    public async Task InvokeAsync(HttpContext context)
    {
        #region log the time and the middleware name
        this._logger.LogDebug("{time}: in Step3_CorsCheck middleware",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
        #endregion


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


        // Apply CORS headers
        ApplyCorsHeaders(context, section);

        // Handle preflight OPTIONS request
        if (context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 204; // No Content
            return; // Short-circuit the pipeline
        }

        // Proceed to the next middleware
        await _next(context);
    }

    private void ApplyCorsHeaders(HttpContext context, IConfigurationSection section)
    {
        // Get the origin from the request header
        var origin = context.Request.Headers["Origin"].ToString();

        // Determine the allowed origin
        string allowedOrigin = DetermineAllowedOrigin(section, origin);

        // Get allowed methods based on route's verb configuration
        string allowedMethods = GetAllowedMethods(section);

        // Get allowed headers (route-specific > global > default)
        string? allowedHeaders = GetAllowedHeaders(section);

        // Set all CORS headers
        context.Response.Headers["Access-Control-Allow-Origin"] = allowedOrigin;
        context.Response.Headers["Access-Control-Allow-Methods"] = allowedMethods;
        if (!string.IsNullOrWhiteSpace(allowedHeaders))
            context.Response.Headers["Access-Control-Allow-Headers"] = allowedHeaders;
        // todo: make this configurable
        context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
        context.Response.Headers["Access-Control-Max-Age"] = "7200"; // 2 hours

        this._logger.LogDebug("CORS headers set: Origin={origin}, Methods={methods}",
            allowedOrigin, allowedMethods);
    }

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
    #endregion

    #region allowed headers determination
    private string? GetAllowedHeaders(IConfigurationSection section)
    {

        var headers = GetCorsValue(section, "allowed_headers");

        if (string.IsNullOrWhiteSpace(headers))
        {
            // Default headers
            this._logger.LogDebug("CORS: Using default allowed headers");
            return null; // _defaultHeaders;
        }

        return headers;
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
            // No verb specified, use default methods
            this._logger.LogDebug("CORS: No verb configuration found, using default allowed methods");
            return _defaultMethods;
        }

        // Parse comma-separated verbs, trim, uppercase, and add OPTIONS
        var verbs = verb.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.ToUpperInvariant())
            .ToHashSet(); // Use HashSet to avoid duplicates

        // Always include OPTIONS for preflight requests
        verbs.Add("OPTIONS");

        var methods = string.Join(", ", verbs.OrderBy(v => v));
        this._logger.LogDebug("CORS: Using route-specific allowed methods: {methods}", methods);
        return methods;

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

