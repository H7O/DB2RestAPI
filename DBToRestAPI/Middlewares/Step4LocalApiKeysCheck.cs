using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DBToRestAPI.Middlewares
{
    /// <summary>
    /// Third middleware in the pipeline that validates route-specific (local) API keys.
    /// 
    /// This middleware checks for API keys that are configured locally within individual route definitions,
    /// providing route-level access control in addition to or instead of global API keys.
    /// 
    /// Required context.Items from previous middlewares:
    /// - `route`: String representing the matched route path
    /// - `section`: IConfigurationSection for the route's configuration
    /// - `service_type`: String indicating service type (`api_gateway` or `db_query`)
    /// 
    /// The middleware:
    /// - Validates local API keys specific to the route (if configured)
    /// - Allows routes to have their own independent API key requirements
    /// - Works in conjunction with global API keys (Step1) for layered security
    /// 
    /// Responses:
    /// - 401 Unauthorized: Local API key validation failed
    /// - 500 Internal Server Error: Required context items missing from previous middlewares
    /// - Passes to next middleware: No local API keys configured or validation successful
    /// </summary>
    public class Step4LocalApiKeysCheck(
        RequestDelegate next,
        SettingsService settings,
        ILogger<Step4LocalApiKeysCheck> logger)
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly ILogger<Step4LocalApiKeysCheck> _logger = logger;
        // private static int count = 0;
        private static readonly string _errorCode = "Step 4 - Local API Keys Check Error";
        public async Task InvokeAsync(HttpContext context)
        {

            #region log the time and the middleware name
            this._logger.LogDebug("{time}: in Step3LocalApiKeysCheck middleware",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
            #endregion

            #region if no section passed from the previous middlewares, return 500
            IConfigurationSection? section = context.Items.ContainsKey("section") 
                ? context.Items["section"] as IConfigurationSection 
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

            #region if local api keys check fails, return the appropriate failure response
            var apiCheckFailure = this._settings.GetFailedAPIKeysCheckResponseIfAny(section, context.Request);

            if (apiCheckFailure != null)
            {
                await context.Response.DeferredWriteAsJsonAsync(apiCheckFailure);
                return;
            }

            #endregion

            await _next(context);

        }
    }
}
