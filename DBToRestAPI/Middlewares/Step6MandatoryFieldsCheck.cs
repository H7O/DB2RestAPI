using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace DBToRestAPI.Middlewares
{
    /// <summary>
    /// Validates mandatory request parameters and prepares data for processing.
    /// 
    /// This middleware parses the request body, validates that all mandatory fields are present,
    /// and prepares the parameters for subsequent middleware (typically database query execution).
    /// 
    /// Required context.Items from previous middlewares:
    /// - `route`: String representing the matched route path
    /// - `section`: IConfigurationSection for the route's configuration
    /// - `service_type`: String indicating service type (`api_gateway` or `db_query`)
    /// 
    /// The middleware:
    /// - Enables request body buffering for multiple reads
    /// - Parses and validates JSON payload format
    /// - Extracts parameters from query string, body, and headers
    /// - Validates mandatory parameters as defined in route configuration
    /// - Prepares parameter collection for database queries or further processing
    /// 
    /// Sets in context.Items for next middleware:
    /// - `parameters`: Complete collection of parameters from all sources (query string, body, headers)
    /// - `payload`: Parsed JSON payload string (if body contains valid JSON)
    /// 
    /// Responses:
    /// - 400 Bad Request: Invalid JSON format, missing mandatory fields, or request cancelled
    /// - 500 Internal Server Error: Required context items missing from previous middlewares
    /// - Passes to next middleware: All validations successful, parameters prepared
    /// </summary>

    public class Step6MandatoryFieldsCheck(
        RequestDelegate next,
        SettingsService settings,
        IConfiguration configuration,
        SettingsEncryptionService settingsEncryptionService,
        ILogger<Step6MandatoryFieldsCheck> logger,
        ParametersBuilder paramsBuilder)
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        // private readonly IConfiguration _configuration = configuration;
        private readonly SettingsEncryptionService _configuration = settingsEncryptionService;
        private readonly ILogger<Step6MandatoryFieldsCheck> _logger = logger;
        private readonly ParametersBuilder _paramsBuilder = paramsBuilder;
        // private static int count = 0;
        private static readonly string _errorCode = "Step 6 - Mandatory Fields Check Error";
        public async Task InvokeAsync(HttpContext context)
        {

            #region log the time and the middleware name

            this._logger.LogDebug("{time}: in Step5MandatoryFieldsCheck middleware",
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

            #region Get content type from context
            // Content type was validated and stored in context.Items by Step2ServiceTypeChecks
            var contentType = context.Items.ContainsKey("content_type")
                ? context.Items["content_type"] as string
                : null;

            if (string.IsNullOrWhiteSpace(contentType))
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Content type not found in context. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );

                return;
            }
            #endregion


            #region check if the request was cancelled
            if (context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Request was cancelled"
                        }
                    )
                    {
                        StatusCode = 400
                    }
                );

                return;
            }
            #endregion
            // retrieve the parameters (which consists of route, query string, form data, json body, and headers parameters)
            var qParams = await this._paramsBuilder.GetParamsAsync();

            if (qParams == null)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Failed to extract parameters from the request"
                        }
                    )
                    {
                        StatusCode = 400
                    }
                );
                return;
            }

            #region check if there are any mandatory parameters missing
            var failedMandatoryCheckResponse = this._settings
                .GetFailedMandatoryParamsCheckIfAny(section, qParams);
            if (failedMandatoryCheckResponse != null) 
            {
                await context.Response.DeferredWriteAsJsonAsync(failedMandatoryCheckResponse);
                return;
            }
            #endregion

            try
            {
                await _next(context);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
            }

        }
    }
}
