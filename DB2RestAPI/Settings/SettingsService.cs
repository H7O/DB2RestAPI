using Microsoft.AspNetCore.Mvc;
using DB2RestAPI.Settings.Extensinos;
using Com.H.Data.Common;
using System.Text.Json;
using DB2RestAPI.Cache;
namespace DB2RestAPI.Settings
{
    public class SettingsService(
        IConfiguration configuration,
        CacheService cacheService)
    {
        private readonly IConfiguration _configuration = configuration;
        private readonly CacheService _cacheService = cacheService;
        public CacheService CacheService => _cacheService;


        #region api keys

        /// <summary>
        /// Check if the request has an API key and if it is valid
        /// </summary>
        /// <param name="section"></param>
        /// <returns>
        /// If the request requires an API key before processing
        /// and the API key is not provided or is invalid,
        /// return a response with status code 401
        /// </returns>
        public ObjectResult? GetFailedAPIKeysCheckResponseIfAny(
            IConfigurationSection section,
            HttpRequest request
            )
        {
            return section.GetFailedAPIKeysCheckResponseIfAny(_configuration, request);
        }




        #endregion

        #region mandatory parameters

        /// <summary>
        /// check if the request has all the mandatory parameters
        /// </summary>
        public ObjectResult? GetFailedMandatoryParamsCheckIfAny(
            IConfigurationSection serviceQuerySection,
            List<QueryParams> qParams
            )
        {
            return serviceQuerySection.GetFailedMandatoryParamsCheckIfAny(qParams,
                serviceQuerySection.GetMandatoryParameters()
                );
        }

        public string[]? GetMandatoryParameters(
            IConfigurationSection serviceQuerySection)
        {
            return serviceQuerySection.GetMandatoryParameters();
        }


        #endregion

        #region parameters

        public List<QueryParams> GetParams(
            IConfigurationSection serviceQuerySection,
            HttpRequest request,
            JsonElement? payload
            )
        {
            return serviceQuerySection.GetParams(_configuration, request, payload);
        }


        #endregion

        #region debug check

        public bool IsDebugMode(HttpRequest request)
        {
            return request.Headers.TryGetValue("debug-mode", out var debugModeHeaderValue)
                                                   && debugModeHeaderValue == this._configuration.GetSection("debug_mode_header_value")?.Value
                                                   && debugModeHeaderValue != Microsoft.Extensions.Primitives.StringValues.Empty;
        }


        #endregion


        #region error handling

        public string GetDefaultGenericErrorMessage()
        {
            var defaultGenericErrorMessage = this._configuration.GetSection("default_generic_error_message")?.Value;
            if (string.IsNullOrWhiteSpace(defaultGenericErrorMessage))
                defaultGenericErrorMessage = "An error occurred while processing your request.";
            return defaultGenericErrorMessage;
        }


        public ObjectResult GetExceptionResponse(HttpRequest request, Exception ex)
        {
            if (ex.InnerException != null
                &&
                typeof(Microsoft.Data.SqlClient.SqlException).IsAssignableFrom(ex.InnerException.GetType())
                )
            {
                Microsoft.Data.SqlClient.SqlException sqlException = (Microsoft.Data.SqlClient.SqlException)ex.InnerException;
                if (sqlException.Number >= 50000 && sqlException.Number < 51000)
                {
                    return new ObjectResult(sqlException.Message)
                    {
                        StatusCode = sqlException.Number - 50000,
                        Value = new
                        {
                            success = false,
                            message = sqlException.Message,
                            error_number = sqlException.Number - 50000
                        }
                    };
                }
            }

            // check if user passed `debug-mode` header in 
            // the request and if it has a value that 
            // corresponds to the value defined in the config file
            // under `debug_mode_header_value` cacheKey
            // if so, return the full error message and stack trace
            // else return a generic error message
            if (this.IsDebugMode(request))
            {

                var errorMsg = $"====== exception ======{Environment.NewLine}"
                    + $"{ex.Message}{Environment.NewLine}{Environment.NewLine}"
                    + $"====== stack trace ====={Environment.NewLine}"
                    + $"{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";

                // return a plain text response in the form of ObjectResult
                return new ObjectResult(errorMsg)
                {
                    StatusCode = 500
                };

                //this.Response.ContentType = "text/plain";
                //this.Response.StatusCode = 500;
                //await this.Response.WriteAsync(errorMsg);
                //await this.Response.CompleteAsync();

            }

            // get `default_generic_error_message` from config file
            // if it is not defined, use a default value `An error occurred while processing your request.`


            return new BadRequestObjectResult(new { success = false, message = GetDefaultGenericErrorMessage() });
        }

        #endregion

    }
}
