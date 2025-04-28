using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DB2RestAPI.Middlewares
{
    /// <summary>
    /// `route`, `section` and `service_type` should already be available in the
    /// context.Items when this middleware is called.
    /// `route` is a string representing the route of the request.
    /// `section` is an IConfigurationSection representing the configuration section of the route.
    /// `service_type` is a string representing the type of service.
    /// Current supported services are `api_gateway` and `db_query`
    /// The purpose of this middleware is to check whether or not there are local API keys (local to the `route`) in the request.
    /// And if so, whether or not they are valid.
    /// </summary>
    public class Step3LocalApiKeysCheck(
        RequestDelegate next,
        SettingsService settings,
        ILogger<Step3LocalApiKeysCheck> logger)
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly ILogger<Step3LocalApiKeysCheck> _logger = logger;
        private static int count = 0;
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
                            message = "Improper service setup. (Contact your service provider support and provide them with error code `SMWSE3`)"
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
