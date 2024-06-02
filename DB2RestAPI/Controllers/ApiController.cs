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
using System.Text;
using System.Net.Http;
using Azure;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.Http;

namespace DB2RestAPI.Controllers
{
    [Produces("application/json")]
    public class ApiController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly DbConnection _connection;
        private static readonly string _defaultVariablesRegex = @"(?<open_marker>\{\{)(?<param>.*?)?(?<close_marker>\}\})"; 
        private static readonly string _defaultHeadersRegex = @"(?<open_marker>\{header\{)(?<param>.*?)?(?<close_marker>\}\})";
        private static readonly string _defaultQueryStringRegex = @"(?<open_marker>\{qs\{)(?<param>.*?)?(?<close_marker>\}\})";

        /// <summary>
        /// exclude `Transfer-Encoding` and `Content-Length` headers
        /// as they are set by the server automatically
        /// and should not be set manually by the proxy
        /// reason for that is that the server will set the `Content-Length` header
        /// based on the actual content length, and if the proxy sets it manually
        /// it may cause issues with the response stream.
        /// And the reason why we exclude `Transfer-Encoding` header is that
        /// the server will set it based on the response content type
        /// and the proxy should not set it manually
        /// as it may cause issues with the response stream.
        /// </summary>
        private static readonly string[] _proxyHeadersToExclude = new string[] { "Transfer-Encoding", "Content-Length" };

            
            
        public ApiController(
            IConfiguration configuration, 
            DbConnection connection)
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
        public async Task<IActionResult> Index(
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

            #region check if configuration is null
            if (this._configuration == null)
                return BadRequest(new { success = false, message = "Configuration is null" });
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


            #region checking if the request is an API gateway routing request
            var routes = this._configuration.GetSection("routes");

            if (routes !=null && routes.Exists())
            {
                var routeSection = routes.GetSection(route);

                if (routeSection != null && routeSection.Exists())
                {
                    return await GetRoutedResponse(routeSection, payload);
                }
            }   

            #endregion

            #region check if endpoint is defined in sql.xml config file


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

            #region check local API keys if defined for the endpoint
            var sqlSectionApiKeysCheckResponse = GetFailedAPIKeysCheckResponseIfAny(serviceQuerySection);
            if (sqlSectionApiKeysCheckResponse != null)
                return sqlSectionApiKeysCheckResponse;
            #endregion


            #region prepare query parameters
            var qParams = GetParams(serviceQuerySection, payload);

            #endregion

            #region check mandatory parameters for SQL queries end points

            var mandatoryParamsCheckResponse = 
                GetFailedMandatoryParamsCheckIfAny(
                    serviceQuerySection, 
                    qParams,
                    mandatoryParameters: GetMadatoryParameters(serviceQuerySection)
                    );
            if (mandatoryParamsCheckResponse != null)
                return mandatoryParamsCheckResponse;

            //var mandatoryParameters = serviceQuerySection
            //    .GetSection("mandatory_parameters")?.Value?
            //    .Split(new char[] { ',', ' ', '\n', '\r' },
            //    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            //if (mandatoryParameters != null && mandatoryParameters.Length > 0)
            //{
            //    var propertyNames = payload.Equals(default) ? null : payload.EnumerateObject().Select(x => x.Name).ToArray();

            //    var missingMandatoryParams = mandatoryParameters.Where(x => !(propertyNames?.Contains(x) == true)).ToArray();

            //    if (missingMandatoryParams.Length > 0)
            //        return BadRequest(new
            //        {
            //            success = false,
            //            message = $"Missing mandatory parameters: {string.Join(",", missingMandatoryParams)}"
            //        });
            //}


            #endregion

            // check if count query is defined
            var countQuery = serviceQuerySection.GetSection("count_query")?.Value;
            int? dbCommandTimeout = 
                serviceQuerySection.GetValue<int?>("db_command_timeout")??
                this._configuration.GetValue<int?>("default_db_command_timeout");
            try
            {
                if (string.IsNullOrWhiteSpace(countQuery))
                    return Ok(this._connection
                        .ExecuteQuery(query, qParams, commandTimeout : dbCommandTimeout)
                        .ToChamberedEnumerable());
                else
                {
                    return Ok(
                        new
                        {
                            success = true,
                            count = ((ExpandoObject?)this._connection
                            .ExecuteQuery(countQuery, qParams, commandTimeout: dbCommandTimeout)
                            .ToList()?.FirstOrDefault())?.FirstOrDefault().Value,
                            data = this._connection
                                .ExecuteQuery(query, qParams, commandTimeout: dbCommandTimeout)
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
                if (this.IsDebugMode())
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

                return BadRequest(new { success = false, message = GetDefaultGenericErrorMessage()});
            }

        }

        
        private async Task<IActionResult> GetRoutedResponse(
                    IConfiguration routeSection,
                    JsonElement payload
                    )
        {
            
            #region local API keys check 
            var failedAPIKeysCheck = GetFailedAPIKeysCheckResponseIfAny(routeSection);
            if (failedAPIKeysCheck != null)
                return failedAPIKeysCheck;
            #endregion
            var url = routeSection.GetValue<string>("url");

            if (!string.IsNullOrWhiteSpace(url))
            {

                var mandatoryParameters = GetMadatoryParameters(routeSection);
                if (mandatoryParameters != null
                    && mandatoryParameters.Length > 0
                    )
                {
                    var qParams = GetParams(routeSection, payload);
                    var failedMandatoryParamsCheck = GetFailedMandatoryParamsCheckIfAny(routeSection, qParams, mandatoryParameters);
                    if (failedMandatoryParamsCheck != null)
                        return failedMandatoryParamsCheck;
                }
                // check if `this.Request` has query string
                // if queryString has values, append it to the url, and if the url already has a query string, append it with `&`
                if (!string.IsNullOrWhiteSpace(this.Request.QueryString.Value))
                {
                    url += (url.Contains("?") ? "&" : "?") + this.Request.QueryString.Value.Substring(1);
                }

                // route the current request (with headers and action to url)
                var request = new HttpRequestMessage(new HttpMethod(this.Request.Method), url);
                request.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

                var passApiKey = routeSection.GetValue<bool?>("pass_api_key") ?? false;

                // get headers from the current request and add them to the new request

                // see if there are headers that should not be passed to the server for this particular route
                var headersToExclude = routeSection.GetValue<string>("headers_to_exclude_from_routing")?
                    .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);


                // .GetSection("headers_to_exclude_from_routing")?.GetChildren().Select(x => x.Value).ToArray();
                if (headersToExclude == null || headersToExclude.Length < 1)
                    // check if there are default headers to exclude for all routes
                    headersToExclude = this._configuration.GetValue<string>("default_headers_to_exclude_from_routing")?
                        .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // .GetSection("default_headers_to_exclude_from_routing")?.GetChildren().Select(x => x.Value).ToArray();

                // see if there are headers to override for this particular route defined in the config file under:
                //       <headers>
                //          <header>
                //              <name>x-api-key</name>
                //              <value>local api key 1</value>
                //          </header>
                //      </headers>
                var headersToOverride = routeSection.GetSection("headers")?.GetChildren()?
                    // remove null `name` headers
                    .Where(x => !string.IsNullOrWhiteSpace(x.GetValue<string>("name")))
                    .Select(x => new KeyValuePair<string, string>(x.GetValue<string>("name")!, 
                    x.GetValue<string>("value")??string.Empty))
                    .ToDictionary(x => x.Key, x => x.Value);

                if (headersToOverride?.Count > 0 == true)
                {
                    foreach (var header in headersToOverride)
                    {
                        _ = request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                foreach (var header in this.Request.Headers)
                {

                    if (
                        // exclude headers that should not be passed to the server (make sure to accomodate for case sensitivity)
                        headersToExclude?.Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
                        || headersToOverride?.Select(x => x.Key).Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
                        )
                        continue;
                    _ = request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
                
                try
                {
                    var ignoreCertificateErrors = routeSection.GetValue<bool?>("ignore_certificate_errors");

                    if (ignoreCertificateErrors == null)
                    {
                        ignoreCertificateErrors = this._configuration.GetValue<bool?>("ignore_certificate_errors_when_routing");
                    }

                    using (var client = ignoreCertificateErrors == true 
                        ? new HttpClient(GetServerCertificateHandlerThatIgnoresErrors())
                        : new HttpClient())

                    {
                        var response = await client.SendAsync(request); // , HttpCompletionOption.ResponseHeadersRead);

                        
                        // proxy the response back to the caller as is (i.e., without processing)

                        // setup the response content headers
                        foreach (var header in response.Content.Headers)
                        {
                            Response.HttpContext.Response.Headers[header.Key] = header.Value.ToArray();
                        }

                        // setup the response headers
                        foreach(var header in response.Headers
                            // exclude `Transfer-Encoding` and `Content-Length` headers
                            // as they are set by the server automatically
                            // and should not be set manually by the proxy
                            // reason for that is that the server will set the `Content-Length` header
                            // based on the actual content length, and if the proxy sets it manually
                            // it may cause issues with the response stream.
                            // And the reason why we exclude `Transfer-Encoding` header is that
                            // the server will set it based on the response content type
                            // and the proxy should not set it manually
                            // as it may cause issues with the response stream.
                            .Where(x=>!new string[] { "Transfer-Encoding", "Content-Length" }.Contains(x.Key))
                            )
                        {
                            Response.Headers[header.Key] = header.Value.ToArray();
                        }
                        
                        // copy the proxy call stream to the response stream
                        await response.Content.CopyToAsync(Response.BodyWriter.AsStream());

                        // complete the response stream
                        Response.BodyWriter.Complete();
                        
                        // return an empty result (since the response is already sent)
                        return new EmptyResult();
                    }


                }
                catch (Exception ex)
                {
                    return GetExceptionResponse(ex);
                }
            }
            return BadRequest(new { success = false, message = $"Improper route settings" });

        }


        /// <summary>
        /// Check if the request has an API key and if it is valid
        /// </summary>
        /// <param name="section"></param>
        /// <returns>
        /// If the request requires an API key before processing
        /// and the API key is not provided or is invalid,
        /// return a response with status code 401
        /// </returns>
        private ObjectResult? GetFailedAPIKeysCheckResponseIfAny(
            IConfiguration section
            )
        {
            if (bool.TryParse(_configuration.GetSection("enable_global_api_keys")?.Value, out bool globalAPICheckEnabled)
                && !globalAPICheckEnabled)
            {
                var apiKeys = GetAPIKeys(section);

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
            return null;

        }


        private string[]? GetMadatoryParameters(IConfiguration serviceQuerySection)
        {
            var mandatoryParameters = serviceQuerySection
                .GetSection("mandatory_parameters")?.Value?
                .Split(new char[] { ',', ' ', '\n', '\r' },
                               StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return mandatoryParameters;
        }
        private ObjectResult? GetFailedMandatoryParamsCheckIfAny(
            IConfiguration serviceQuerySection,
            List<QueryParams> qParams,
            string[]? mandatoryParameters = null
            
            )
        {
            if (mandatoryParameters == null || mandatoryParameters.Length < 1)
                return null;

            List<string> keys = new List<string>();
            foreach (var qParam in qParams)
            {
                IDictionary<string, object>? model = qParam.DataModel?.GetDataModelParameters();
                if (model == null) continue;

                keys = keys.Union(model.Keys).ToList();
            }

            var missingMandatoryParams = mandatoryParameters.Where(x => !(keys.Contains(x) == true)).ToArray();

            if (missingMandatoryParams.Length > 0)
                return BadRequest(new
                {
                    success = false,
                    message = $"Missing mandatory parameters: {string.Join(",", missingMandatoryParams)}"
                });
            
            return null;

        }

        private string[] GetAPIKeys(IConfiguration section)
        {
            var apiKeysSection = section.GetSection("api_keys:key");

            if (apiKeysSection != null
                               && apiKeysSection.Exists())
            {
                return apiKeysSection.GetChildren().Select(x => x.Value??"")
                    .Where(x=>!string.IsNullOrWhiteSpace(x))
                    .ToArray();
            }
            return Array.Empty<string>();
        }

        private bool IsDebugMode()
        {
            return Request.Headers.TryGetValue("debug-mode", out var debugModeHeaderValue)
                                                   && debugModeHeaderValue == this._configuration.GetSection("debug_mode_header_value")?.Value
                                                   && debugModeHeaderValue != Microsoft.Extensions.Primitives.StringValues.Empty;
        }

        private string GetDefaultGenericErrorMessage()
        {
            var defaultGenericErrorMessage = this._configuration.GetSection("default_generic_error_message")?.Value;
            if (string.IsNullOrWhiteSpace(defaultGenericErrorMessage))
                defaultGenericErrorMessage = "An error occurred while processing your request.";
            return defaultGenericErrorMessage;
        }

        private ObjectResult GetExceptionResponse(Exception ex)
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
            if (this.IsDebugMode())
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


            return BadRequest(new { success = false, message = GetDefaultGenericErrorMessage() });
        }

        private List<QueryParams> GetParams(IConfiguration serviceQuerySection, JsonElement payload)
        {
            #region prepare query parameters
            var qParams = new List<QueryParams>();

            #region headers
            // add headers to qParams
            var headersRegex = serviceQuerySection.GetSection("headers_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(headersRegex))
                headersRegex = this._configuration.GetSection("default_headers_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(headersRegex))
                headersRegex = _defaultHeadersRegex;

            if (this.Request.Headers?.Count > 0 == true)
            {
                qParams.Add(new QueryParams()
                {
                    DataModel = this.Request.Headers
                    .ToDictionary(x => x.Key, x => string.Join("|", x.Value.Where(x => !string.IsNullOrEmpty(x)))),
                    QueryParamsRegex = headersRegex
                });
            }
            else
            {
                // add custom header with unlikely name to help
                // set header variables in the query to DbNull.Value
                // in the event that headers are not passed
                // and the user has header variables in the query
                // which if left unset will cause an error
                qParams.Add(new QueryParams()
                {
                    DataModel = new Dictionary<string, string> { { "unlikely_header_to_be_passed", "123" } },
                    QueryParamsRegex = headersRegex
                });

            }

            #endregion

            #region query string
            // add query string to qParams
            var queryStringRegex = serviceQuerySection.GetSection("query_string_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(queryStringRegex))
                queryStringRegex = this._configuration.GetSection("default_query_string_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(queryStringRegex))
                queryStringRegex = _defaultQueryStringRegex;

            if (this.Request.Query?.Count > 0 == true)
            {
                qParams.Add(new QueryParams()
                {
                    DataModel = this.Request
                    .Query
                    .ToDictionary(x => x.Key, x => string.Join("|", x.Value.Where(x => !string.IsNullOrEmpty(x)))),
                    QueryParamsRegex = queryStringRegex
                });
            }
            else
            {
                // add custom query string with unlikely name to help
                // set URL query string variables in the SQL query to DbNull.Value
                // in the event that no URL query string variables were passed
                // and the user has URL query string variables in the SQL query
                // which if left unset will cause an error
                qParams.Add(new QueryParams()
                {
                    DataModel = new Dictionary<string, string> { { "unlikely_query_string_to_be_passed", "123" } },
                    QueryParamsRegex = queryStringRegex
                });

            }
            #endregion

            #region payload
            var varRegex = serviceQuerySection.GetSection("variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(varRegex))
                varRegex = this._configuration.GetSection("default_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(varRegex))
                varRegex = _defaultVariablesRegex;

            if (!payload.Equals(default)
                && payload.ValueKind != JsonValueKind.Null
                && payload.ValueKind != JsonValueKind.Undefined
                )
            {
                qParams.Add(new QueryParams()
                {
                    DataModel = payload,
                    QueryParamsRegex = varRegex
                });
            }
            else 
            { 
                // add custom payload with unlikely name to help
                // set payload variables in the SQL query to DbNull.Value
                // in the event that no payload variables were passed
                // and the user has payload variables in the SQL query
                // which if left unset will cause an error
                qParams.Add(new QueryParams()
                {
                    DataModel = new Dictionary<string, string> { { "unlikely_payload_to_be_passed", "123" } },
                    QueryParamsRegex = varRegex
                });
            }

            #endregion

            return qParams;
            #endregion
        }

        /// <summary>
        /// Returns a handler that ignores server certificate errors.
        /// Helpful for scenarios where the server certificate is not trusted
        /// or is self-signed.
        /// </summary>
        /// <returns></returns>
        private HttpClientHandler GetServerCertificateHandlerThatIgnoresErrors()
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
            return handler;
        }

    }
}
