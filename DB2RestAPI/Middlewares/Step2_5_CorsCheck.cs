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
public class Step2_5_CorsCheck(
    RequestDelegate next,
    IConfiguration configuration,
    ILogger<Step2_5_CorsCheck> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<Step2_5_CorsCheck> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        #region log the time and the middleware name
        this._logger.LogDebug("{time}: in Step2_5_CorsCheck middleware",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
        #endregion

        // Get the origin from the request
        var origin = context.Request.Headers["Origin"].ToString();

        // Try to get route-specific CORS config first
        IConfigurationSection? corsConfig = null;

        // Check if we have a section from previous middleware
        if (context.Items.TryGetValue("section", out var sectionValue) 
            && sectionValue is IConfigurationSection section)
        {
            corsConfig = section.GetSection("cors");
        }

        // Fall back to global CORS config if route-specific config not found or doesn't exist
        if (corsConfig == null || !corsConfig.Exists())
        {
            corsConfig = _configuration.GetSection("cors");
        }

        if (!string.IsNullOrWhiteSpace(origin))
        {
            // Apply CORS headers for browser requests (with Origin header)
            ApplyCorsHeaders(context, origin, corsConfig);
        }
        else
        {
            // No Origin header - non-browser request (e.g., Postman, curl, server-to-server)
            // Use fallback_origin if available to avoid auditor complaints about "*"
            string allowedOrigin = "*"; // Default
            
            if (corsConfig != null && corsConfig.Exists())
            {
                var fallbackOrigin = corsConfig.GetValue<string>("fallback_origin");
                if (!string.IsNullOrWhiteSpace(fallbackOrigin))
                {
                    allowedOrigin = fallbackOrigin.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? fallbackOrigin
                        : $"https://{fallbackOrigin}";
                    this._logger.LogDebug("CORS: No Origin header, using fallback_origin '{fallback}' for non-browser request", allowedOrigin);
                }
                else
                {
                    this._logger.LogDebug("CORS: No Origin header and no fallback_origin configured, using '*' for non-browser request");
                }
            }
            
            context.Response.Headers["Access-Control-Allow-Origin"] = allowedOrigin;
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Api-Key, X-Requested-With";
        }

        // Handle preflight OPTIONS request
        if (context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 204; // No Content
            return; // Short-circuit the pipeline
        }

        // Proceed to the next middleware
        await _next(context);
    }

    private void ApplyCorsHeaders(HttpContext context, string origin, IConfigurationSection? corsConfig)
    {
        string allowedOrigin = "*"; // Default to allow all

        if (corsConfig != null && corsConfig.Exists())
        {
            var pattern = corsConfig.GetValue<string>("pattern");
            var fallbackOrigin = corsConfig.GetValue<string>("fallback_origin");

            if (!string.IsNullOrWhiteSpace(pattern))
            {
                try
                {
                    // Extract domain from origin (remove protocol)
                    var originUri = new Uri(origin);
                    var originDomain = originUri.Host;

                    // Check if origin matches the regex pattern
                    if (Regex.IsMatch(originDomain, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    {
                        // Origin matches pattern - allow the specific origin
                        allowedOrigin = origin;
                        this._logger.LogDebug("CORS: Origin '{origin}' matches pattern '{pattern}'", origin, pattern);
                    }
                    else if (!string.IsNullOrWhiteSpace(fallbackOrigin))
                    {
                        // Origin doesn't match - use fallback
                        allowedOrigin = fallbackOrigin.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? fallbackOrigin
                            : $"https://{fallbackOrigin}";
                        this._logger.LogDebug("CORS: Origin '{origin}' doesn't match pattern, using fallback '{fallback}'",
                            origin, allowedOrigin);
                    }
                    else
                    {
                        // No fallback - deny by setting to null origin
                        allowedOrigin = "null";
                        this._logger.LogWarning("CORS: Origin '{origin}' doesn't match pattern '{pattern}' and no fallback defined",
                            origin, pattern);
                    }
                }
                catch (Exception ex)
                {
                    this._logger.LogError(ex, "CORS: Error processing origin '{origin}' with pattern '{pattern}'", origin, pattern);
                    // On error, use fallback or deny
                    allowedOrigin = !string.IsNullOrWhiteSpace(fallbackOrigin)
                        ? (fallbackOrigin.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? fallbackOrigin : $"https://{fallbackOrigin}")
                        : "null";
                }
            }
            else if (!string.IsNullOrWhiteSpace(fallbackOrigin))
            {
                // No pattern defined, just use fallback as the allowed origin
                allowedOrigin = fallbackOrigin.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? fallbackOrigin
                    : $"https://{fallbackOrigin}";
            }
            // else: no pattern and no fallback, keep default "*"
        }
        // else: no CORS config at all, keep default "*"

        // Set CORS headers
        context.Response.Headers["Access-Control-Allow-Origin"] = allowedOrigin;
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Api-Key, X-Requested-With";
        context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
        context.Response.Headers["Access-Control-Max-Age"] = "7200"; // 2 hours

        this._logger.LogDebug("CORS headers set: Access-Control-Allow-Origin = {allowedOrigin}", allowedOrigin);
    }
}

