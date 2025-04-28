using Microsoft.AspNetCore.Mvc;
using DB2RestAPI.Settings.Extensinos;

namespace DB2RestAPI.Middlewares
{
    /// <summary>
    /// This middleware checks if the request contains a global API key if the global API keys are enabled.
    /// If the API key is not provided, it returns a 401 Unauthorized response.
    /// 
    /// </summary>
    public class Step1GlobalApiKeysCheck(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<Step1GlobalApiKeysCheck> logger)
    {
        private readonly RequestDelegate _next = next;
        private readonly IConfiguration _configuration = configuration;
        private readonly ILogger<Step1GlobalApiKeysCheck> _logger = logger;
        const string APIKEY = "x-api-key";

        public async Task InvokeAsync(HttpContext context)
        {
            this._logger.LogDebug("{time}: in Step1GlobalApiKeysCheck middleware", 
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
