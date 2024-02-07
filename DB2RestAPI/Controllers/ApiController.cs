using Com.H.Data.Common;
using Microsoft.AspNetCore.Mvc;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.Xml;
using System.Text.Json;
using Com.H.Collections.Generic;
using static System.Net.Mime.MediaTypeNames;
using System.Dynamic;
using Microsoft.IdentityModel.Tokens;

namespace DB2RestAPI.Controllers
{
    [Produces("application/json")]
    public class ApiController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly DbConnection _connection;
        public ApiController(IConfiguration configuration, DbConnection connection)
        {
            _configuration = configuration;
            _connection = connection;
        }

        [Produces("application/json")]
        [HttpGet]
        [HttpPost]
        [HttpDelete]
        [HttpPut]
        [Route("{*route}")]
        public async Task<ActionResult> Index(
            [FromBody] JsonElement payload)
        {
            #region extract route data from the request
            // get route data from the request and extract the api endpoint name
            // from multiple segments separated by `/`
            // then replace `$2F` with `/` in the endpoint name
            var route = this.RouteData.Values["route"]?
                .ToString()?
                .Replace("$2F", "/");
            #endregion


            #region check api endpoint name and payload
            if (string.IsNullOrWhiteSpace(route))
                return BadRequest(new { success = false, message = "No API Endpoint specified" });
            if (route.Length > 500)
                return ValidationProblem(new ValidationProblemDetails
                {
                    Detail = "Endpoint route is too long",
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Endpoint route is too long",
                });


            #endregion

            #region check if endpoint is defined in sql.xml config file

            if (this._configuration == null)
                return BadRequest(new { success = false, message = "Configuration is null" });

            var queries = this._configuration.GetSection("queries");

            if (queries == null || !queries.Exists())
                return BadRequest(new { success = false, message = "No API Endpoints defined" });

            var serviceQuerySection = queries.GetSection(route);

            if (serviceQuerySection == null || !serviceQuerySection.Exists())
                return NotFound(new { success = false, message = $"API Endpoint `{route}` not found" });

            var serviceQuery = serviceQuerySection.GetSection("query");

            var query = serviceQuery?.Value;

            if (string.IsNullOrWhiteSpace(query))
                return BadRequest(new { success = false, message = $"Service `{route}` not yet implemented" });

            #endregion

            #region check local API keys

            if (bool.TryParse(_configuration.GetSection("enable_global_api_keys")?.Value, out bool globalAPICheckEnabled)
                && !globalAPICheckEnabled)
            {
                var apiKeysSection = serviceQuerySection.GetSection("api_keys:key");

                if (apiKeysSection != null
                    && apiKeysSection.Exists()
                    // && !(this.Request?.Path.Value?.StartsWith("/swagger") == true)
                    )
                {
                    var apiKeys = apiKeysSection.GetChildren().Select(x => x.Value).ToArray();

                    if (apiKeys.Length > 0)
                    {
                        if (this.Request == null
                            ||
                            !this.Request.Headers.TryGetValue("x-api-key", out
                            var extractedApiKey))
                        {
                            return new ObjectResult(new { success = false, message = "API key was not provided" })
                            {
                                StatusCode = 401
                            };
                        }

                        if (!apiKeys.Any(x => x?.Equals(extractedApiKey.ToString()) == true))
                        {
                            //this.Response.StatusCode = 401;
                            //this.Response.ContentType = "application/json";
                            //await this.Response.WriteAsync(@"{""success"":false, ""message"":""Unauthorized client""}");
                            return new ObjectResult(new { success = false, message = "Unauthorized client" })
                            {
                                StatusCode = 401
                            };
                        }
                    }
                }
            }
            #endregion


            #region check mandatory parameters
            var mandatoryParameters = serviceQuerySection
                .GetSection("mandatory_parameters")?.Value?
                .Split(new char[] { ',', ' ', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (mandatoryParameters != null && mandatoryParameters.Length > 0)
            {
                var propertyNames = payload.Equals(default) ? null : payload.EnumerateObject().Select(x => x.Name).ToArray();

                var missingMandatoryParams = mandatoryParameters.Where(x => !(propertyNames?.Contains(x) == true)).ToArray();

                if (missingMandatoryParams.Length > 0)
                    return BadRequest(new
                    {
                        success = false,
                        message = $"Missing mandatory parameters: {string.Join(",", missingMandatoryParams)}"
                    });
            }


            #endregion

            #region prepare query parameters
            var qParams = new List<QueryParams>();
            var customVarRegex = serviceQuerySection.GetSection("variables_regex")?.Value;
            if(string.IsNullOrWhiteSpace(customVarRegex))
                customVarRegex = this._configuration.GetSection("default_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(customVarRegex))
                qParams.Add(new QueryParams()
                {
                    DataModel = payload
                });
            else 
                qParams.Add(new QueryParams()
                {
                    DataModel = payload,
                    QueryParamsRegex = customVarRegex
                });

            // add headers to qParams
            var headers = this.Request.Headers;
            if (headers != null && headers.Count > 0)
            {
                foreach (var header in headers)
                {
                    qParams.Add(new QueryParams()
                    {
                        DataModel = new Dictionary<string, object>()
                        {
                            { header.Key, string.Join("|", header.Value.Where(x=>!string.IsNullOrEmpty(x))) }
                        },
                        QueryParamsRegex = @"(?<open_marker>\{header\{)(?<param>.*?)?(?<close_marker>\}\})"
                    });
                }
            }


            #endregion

            // check if count query is defined
            var countQuery = serviceQuerySection.GetSection("count_query")?.Value;
            try
            {
                if (string.IsNullOrWhiteSpace(countQuery))
                    return Ok(this._connection
                        .ExecuteQuery(query, qParams)
                        .ToChamberedEnumerable());
                else
                {
                    return Ok(
                        new
                        {
                            success = true,
                            count = ((ExpandoObject?)this._connection
                            .ExecuteQuery(countQuery, qParams)
                            .ToList()?.FirstOrDefault())?.FirstOrDefault().Value,
                            data = this._connection
                                .ExecuteQuery(query, qParams)
                                .ToChamberedEnumerable()
                        });
                }
            }
            catch (Exception ex)
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
                // under `debug_mode_header_value` key
                // if so, return the full error message and stack trace
                // else return a generic error message
                if (Request.Headers.TryGetValue("debug-mode", out var debugModeHeaderValue)
                                       && debugModeHeaderValue == this._configuration.GetSection("debug_mode_header_value")?.Value
                                       && debugModeHeaderValue != Microsoft.Extensions.Primitives.StringValues.Empty
                                       )
                {

                    var errorMsg = $"====== exception ======{Environment.NewLine}"
                        + $"{ex.Message}{Environment.NewLine}{Environment.NewLine}"
                        + $"====== stack trace ====={Environment.NewLine}"
                        + $"{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";

                    this.Response.ContentType = "text/plain";
                    this.Response.StatusCode = 500;
                    await this.Response.WriteAsync(errorMsg);
                    await this.Response.CompleteAsync();

                }
                //if (bool.TryParse(this._configuration.GetSection("debug_mode")?.Value, out bool debugMode)
                //                       && debugMode)
                //{
                //    return BadRequest(new { success = false, message = ex.Message, stack_trace = ex.StackTrace });
                //}

                // get `default_generic_error_message` from config file
                // if it is not defined, use a default value `An error occurred while processing your request.`
                
                var defaultGenericErrorMessage = this._configuration.GetSection("default_generic_error_message")?.Value;
                if (string.IsNullOrWhiteSpace(defaultGenericErrorMessage))
                    defaultGenericErrorMessage = "An error occurred while processing your request.";

                return BadRequest(new { success = false, message = defaultGenericErrorMessage });
            }

        }



    }
}
