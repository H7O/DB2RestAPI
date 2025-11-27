using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DBToRestAPI.Middlewares
{
    /// <summary>
    /// Validates route-specific API keys.
    /// 
    /// This middleware checks for API keys that are configured within individual route definitions,
    /// providing route-level access control.
    /// 
    /// Required context.Items from previous middlewares:
    /// - `route`: String representing the matched route path
    /// - `section`: IConfigurationSection for the route's configuration
    /// - `service_type`: String indicating service type (`api_gateway` or `db_query`)
    /// 
    /// The middleware:
    /// - Validates API keys specific to the route (if configured)
    /// - Allows routes to have their own independent API key requirements
    /// 
    /// Responses:
    /// - 401 Unauthorized: Local API key validation failed
    /// - 500 Internal Server Error: Required context items missing from previous middlewares
    /// - Passes to next middleware: No local API keys configured or validation successful
    /// </summary>
    public class Step3ApiKeysCheck(
        RequestDelegate next,
        SettingsService settings,
        IConfiguration configuration,
        ILogger<Step3ApiKeysCheck> logger,
        SettingsEncryptionService settingsEncryptionService,
        ApiKeysService apiKeysService)
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly ILogger<Step3ApiKeysCheck> _logger = logger;
        // private readonly IConfiguration _configuration = configuration;
        private readonly ApiKeysService _apiKeysService = apiKeysService;
        private readonly SettingsEncryptionService _configuration = settingsEncryptionService;
        // private static int count = 0;
        private static readonly string _errorCode = "Step 4 - API Keys Check Error";
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

            #region check if api keys collections are configured for this route

            // collection is the section name that contains the api keys for this route
            // each collection is defined under "api_keys_collections:<collection_name>" key in _configuration
            // each collection contains multiple api keys like so:
            /*
            <settings>
              <api_keys_collections>
                <external_vendors>
                  <key>api key 1</key>
                  <key>api key 2</key>
                </external_vendors>
                <internal_solutions>
                  <key>api key 3</key>
                </internal_solutions>
              </api_keys_collections>
            </settings> 
            */
            // where "external_vendors" and "internal_solutions" are collection names defined in the route configuration section
            // in a comma separated list
            // below is to extract those collection names from the route configuration section
            var apiKeysCollections = section.GetValue<string>("api_keys_collections")?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();
            if (apiKeysCollections is null || apiKeysCollections.Count < 1)
            {
                await _next(context);
                return;
            }

            #endregion

            #region check if the request has `x-api-key`
            if (context.Request == null
                ||
                !context.Request.Headers.TryGetValue("x-api-key", out
                var extractedApiKey)
                ||
                extractedApiKey.Count < 1
                ||
                string.IsNullOrWhiteSpace(extractedApiKey[0])
                )
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(new { success = false, message = "API key was not provided" })
                    {
                        // 401 Unauthorized
                        StatusCode = 401
                    }
                );
                return;
            }

            var extractedKeyValue = extractedApiKey[0];

            #endregion

            #region lookup collections in the configuration and aggregate all api keys to see if any match

            // Use the ApiKeysService for validation
            if (_apiKeysService.IsValidApiKeyInCollections(apiKeysCollections, extractedKeyValue!))
            {
                await _next(context);
                return;
            }

            // if reached here, no api key matched
            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(new { success = false, message = "Unauthorized client" })
                {
                    // 401 Unauthorized
                    StatusCode = 401
                }
            );

            #endregion

            
        }


    }
}
