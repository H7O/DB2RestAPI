using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;

namespace DB2RestAPI.Middlewares
{
    public class Step6FileDownloadManagement(
        RequestDelegate next,
        SettingsService settings,
        IConfiguration configuration,
        ILogger<Step6FileDownloadManagement> logger
            )
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly IConfiguration _configuration = configuration;
        private readonly ILogger<Step6FileDownloadManagement> _logger = logger;
        // private static int count = 0;
        private static readonly string _errorCode = "Step 6 - File Download Management Error";
        public async Task InvokeAsync(HttpContext context)
        {
            #region log the time and the middleware name
            this._logger.LogDebug("{time}: in Step6FileDownloadManagement middleware",
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

            #region get settings for file management
            // Get file management settings from `file_management` section
            var fileManagementSection = section.GetSection("file_management");
            var defaultFileStoresSettings = this._configuration.GetSection("file_management");
            if (!fileManagementSection.Exists()
                && !defaultFileStoresSettings.Exists())
            {
                // No file management settings found, proceed to next middleware
                await this._next(context);
                return;
            }

            #endregion

            #region check if `response_type` is set to `file`
            var responseType = fileManagementSection.GetValue<string>("response_type");
            if (string.IsNullOrWhiteSpace(responseType))
                responseType = defaultFileStoresSettings.GetValue<string>("file_management:response_type");

            if (string.IsNullOrWhiteSpace(responseType))
                responseType = "json";

            if (!StringComparer.OrdinalIgnoreCase.Equals(responseType, "file"))
            {
                // Not a file download request, proceed to next middleware
                await this._next(context);
                return;
            }

            #endregion

            #region set context items for ApiController to handle file download instead of returning json

            context.Items["is_file_download"] = true;

            #endregion


            // Proceed to the next middleware
            await _next(context);

        }


    }
}
