using Microsoft.AspNetCore.Mvc;
using DBToRestAPI.Settings.Extensinos;

namespace DBToRestAPI.Middlewares
{
    /// <summary>
    /// First middleware in the pipeline that validates global API keys for incoming requests.
    /// 
    /// This middleware enforces global API key authentication when enabled in configuration.
    /// It checks for the presence of the 'x-api-key' header and validates it against the 
    /// configured list of authorized API keys.
    /// 
    /// Configuration:
    /// - Controlled by the 'enable_global_api_keys' setting (default: false)
    /// - When disabled, all requests pass through without API key validation
    /// - When enabled, requires 'x-api-key' header with a valid key from 'api_keys:key' configuration
    /// 
    /// Responses:
    /// - 401 Unauthorized: API key missing or invalid
    /// - 500 Internal Server Error: API keys configuration section not properly defined
    /// - Passes to next middleware: API key validation successful or global API keys disabled
    /// </summary>
    public class Step1GlobalApiKeysCheckDepricated(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<Step1GlobalApiKeysCheckDepricated> logger)
    {
        private readonly RequestDelegate _next = next;
        private readonly IConfiguration _configuration = configuration;
        private readonly ILogger<Step1GlobalApiKeysCheckDepricated> _logger = logger;
        const string APIKEY = "x-api-key";

        public async Task InvokeAsync(HttpContext context)
        {
            // Temporarily disable this middleware
            await _next(context).ConfigureAwait(false);
            return;
            
            this._logger.LogDebug("{time}: in Step1GlobalApiKeysCheckDepricated middleware", 
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));

            if (bool.TryParse(_configuration.GetSection("enable_global_api_keys")?.Value, out bool checkAPIKeys)
                && !checkAPIKeys)
            {
                await _next(context);
                return;
            }

            if (context.Request == null
                ||
                !context.Request.Headers.TryGetValue(APIKEY, out
                var extractedApiKey))
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

            var apiKeysSection = _configuration.GetSection("api_keys:key");
            if (apiKeysSection == null || !apiKeysSection.Exists())
            {

                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(new { success = false, message = "API keys section is not defined" })
                    {
                        // 500 Internal Server Error
                        StatusCode = 500
                    }
                );

                return;
            }

            if (!apiKeysSection.GetChildren()
                .Any(x => x.Value?.Equals(extractedApiKey.ToString()) == true))
            {

                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(new { success = false, message = "Unauthorized client" })
                    {
                        // 401 Unauthorized
                        StatusCode = 401
                    }
                );

                return;
            }

            await _next(context);
        }
        
    }
}
