using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace DB2RestAPI.Middlewares
{
    /// <summary>
    /// route, section and service_type should already be available 
    /// in the context.Items when this middleware is called.
    /// `route` is a string representing the route of the request.
    /// `section` is an IConfigurationSection representing the configuration section of the route.
    /// `service_type` is a string representing the type of service.
    /// Current supported services are `api_gateway` and `db_query`
    /// 
    /// This middleware checks if the request body contains all the mandatory fields.
    /// If the request body does not contain all the mandatory fields, it returns a 400 Bad Request response.
    /// 
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
        private static int count = 0;
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
                            message = "Improper service setup. (Contact your service provider support and provide them with error code `SMWSE4`)"
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

            JsonElement? payload = null;

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
                            payload = document.RootElement;
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
            var qParams = this._settings.GetParams(section, context.Request, payload);

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
            context.Items["payload"] = payload;


            await _next(context);

        }
    }
}
