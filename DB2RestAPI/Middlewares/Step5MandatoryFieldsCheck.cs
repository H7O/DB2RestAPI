using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace DB2RestAPI.Middlewares
{
    /// <summary>
    /// Fifth middleware in the pipeline that validates mandatory request parameters and prepares data for processing.
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

    public class Step5MandatoryFieldsCheck(
        RequestDelegate next,
        SettingsService settings,
        ILogger<Step5MandatoryFieldsCheck> logger
            )
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly ILogger<Step5MandatoryFieldsCheck> _logger = logger;
        // private static int count = 0;
        private static readonly string _errorCode = "Step 5 - Mandatory Fields Check Error";
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

            #region parse the request body
            var cToken = context.RequestAborted;

            if (cToken.IsCancellationRequested)
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

            // enable buffering for the request body
            // this is necessary because the request body is read multiple times
            // and we need to reset the stream position for the next middleware
            context.Request.EnableBuffering();

            string? jsonPayloadString = null;

            // Read the request body as a string
            // why leaveOpen is true?
            // because we need to reset the stream position for the next middleware
            // and we need to keep the stream open for the next middleware
            using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
            {
                var body = await reader.ReadToEndAsync(cToken);

                context.Request.Body.Position = 0; // Reset the stream position for the next middleware

                if (cToken.IsCancellationRequested)
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

                
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        // Parse the string into a JsonDocument
                        using (JsonDocument document = JsonDocument.Parse(body))
                        {
                            jsonPayloadString = document.RootElement.ToString();
                        }
                    }
                    catch (JsonException)
                    {
                        // Handle the case where the body is not valid JSON

                        await context.Response.DeferredWriteAsJsonAsync(
                            new ObjectResult(
                                new
                                {
                                    success = false,
                                    message = "Invalid JSON format"
                                }
                            )
                            {
                                StatusCode = 400
                            }
                        );

                        return; // Stop further processing
                    }
                }
            }

            #endregion

            // retrieve the parameters (which consists of query string parameters, body parameters and headers)
            var qParams = this._settings.GetParams(section, context, jsonPayloadString);

            #region check if there are any mandatory parameters missing
            var failedMandatoryCheckResponse = this._settings
                .GetFailedMandatoryParamsCheckIfAny(section, qParams);
            if (failedMandatoryCheckResponse != null) 
            {
                await context.Response.DeferredWriteAsJsonAsync(failedMandatoryCheckResponse);
                return;
            }
            #endregion

            // Add the parameters and the payload to the context.Items
            context.Items["parameters"] = qParams;
            if (!string.IsNullOrWhiteSpace(jsonPayloadString))
                context.Items["payload"] = jsonPayloadString;


            await _next(context);

        }
    }
}
