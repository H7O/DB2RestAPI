using Com.H.Data.Common;
using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;

namespace DB2RestAPI.Middlewares
{
    public class Step7FileDownloadManagement(
        RequestDelegate next,
        SettingsService settings,
        IConfiguration configuration,
        ILogger<Step7FileDownloadManagement> logger
            )
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly IConfiguration _configuration = configuration;
        private readonly ILogger<Step7FileDownloadManagement> _logger = logger;
        // private static int count = 0;
        private static readonly string _errorCode = "Step 7 - File Download Management Error";
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

            #region check if `response_structure` is set to `file`
            var responseStructure = section.GetValue<string>("response_structure");
            if (string.IsNullOrWhiteSpace(responseStructure))
                responseStructure = defaultFileStoresSettings.GetValue<string>("file_management:response_type");

            if (!StringComparer.OrdinalIgnoreCase.Equals(responseStructure, "file"))
            {
                // Not a file download request, proceed to next middleware
                await this._next(context);
                return;
            }

            #endregion

            #region set context items and prepare query parameters for file download

            context.Items["is_file_download"] = true;
            // get the store if available
            var fileStore = fileManagementSection.GetValue<string>("store");
            if (string.IsNullOrWhiteSpace(fileStore))
            {
                await _next(context);
                return;
            }

            var fileVariablesPattern = fileManagementSection.GetValue<string>("file_variables_pattern");
            if (string.IsNullOrWhiteSpace(fileVariablesPattern))
                _configuration.GetValue<string>("regex:file_variables_pattern");
            if (string.IsNullOrWhiteSpace(fileVariablesPattern))
                fileVariablesPattern = DefaultRegex.DefaultFileVariablesPattern;


            var qParams = context.Items["parameters"] as List<DbQueryParams>;
            if (qParams == null)
            {
                qParams = new List<DbQueryParams>();
                context.Items["parameters"] = qParams;
            }

            Dictionary<string, object> dataModel = new Dictionary<string, object>
            {
                { "store", fileStore! },
                { "base_path", null }
            };

            qParams.Add(new DbQueryParams
            {
                DataModel = dataModel,
                QueryParamsRegex = fileVariablesPattern
            });

            // get the file store section from settings

            var fileStoreSection = this._configuration.GetSection($"file_management:local_file_store:{fileStore}");
            if (!fileStoreSection.Exists())
            {
                fileStoreSection = this._configuration.GetSection($"file_management:sftp_file_store:{fileStore}");
                if (!fileStoreSection.Exists())
                {
                    await _next(context);
                    return;
                }
                context.Items["sftp_file_store_section"] = fileStoreSection;
            }
            else
            {
                context.Items["local_file_store_section"] = fileStoreSection;
            }
            var path = fileStoreSection.GetValue<string>("base_path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                dataModel["base_path"] = path;
                context.Items["base_path"] = path;
            }


            #endregion

            // Proceed to the next middleware
            await _next(context);

        }

    }
}
