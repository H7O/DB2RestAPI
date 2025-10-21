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
using DB2RestAPI.Cache;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using DB2RestAPI.Settings;
using Microsoft.Data.SqlClient;

namespace DB2RestAPI.Controllers
{

    /// <summary>
    /// route, section, service_type, parameters, and payload should already be available  
    /// in the context.Items when this middleware is called.
    /// `route` is a string representing the route of the request.
    /// `section` is an IConfigurationSection representing the configuration section of the route.
    /// `service_type` is a string representing the type of service. Current supported services 
    /// are `api_gateway` and `db_query`, however `db_query` is the only one that is currently acceptable 
    /// for this controller since if the `service_type` is `api_gateway` then 
    /// the request should have been handled by the `Step4APIGatewayProcess` middleware.
    /// `parameters` is a List<Com.H.Data.Common.QueryParams> representing the parameters of the request.
    /// Which includes the query string parameters, the body parameters, the route parameters and headers parameters.
    /// `payload` is a JsonElement representing the body of the request.
    /// </summary>

    public class ApiController(
        IConfiguration configuration,
        DbConnection connection,
        SettingsService settingsService,
        ILogger<ApiController> logger

            //,
            //Com.H.CacheService.MemoryCache cacheService
            ) : ControllerBase
    {
        private readonly IConfiguration _configuration = configuration;
        private readonly DbConnection _connection = connection;

        private readonly ILogger<ApiController> _logger = logger;


        private readonly SettingsService _settings = settingsService;

        private static readonly string _errorCode = "API Controller Error";



        ///// <summary>
        ///// Similar to Index method but expects its endpoint to have a prefix of `json/`
        ///// Ignores the `Content-Type` header and processes the request payload as JSON
        ///// by default. The request is passed to the Index method for processing.
        ///// </summary>
        ///// <param name="payload">Payload to be passed to Index method under the hood</param>
        ///// <returns>Payload return from Index method</returns>
        //[HttpGet]
        //[HttpPost]
        //[HttpDelete]
        //[HttpPut]
        //[Route("json/{*route}")]
        //public async Task<IActionResult> JsonProxy(
        //    [ModelBinder(BinderType = typeof(BodyModelBinder))] JsonElement payload,
        //    CancellationToken cancellationToken
        //    )
        //{
        //    return await Index();
        //}





        [Produces("application/json")]
        [HttpGet]
        [HttpPost]
        [HttpDelete]
        [HttpPut]
        [Route("{*route}")]
        public async Task<IActionResult> Index(
            )
        {
            #region log the time and the method name
            this._logger.LogDebug("{time}: in ApiController.Index method",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
            #endregion

            #region if no section passed from the previous middlewares, return 500
            IConfigurationSection? section = HttpContext.Items.ContainsKey("section")
                ? HttpContext.Items["section"] as IConfigurationSection
                : null;

            if (section == null || !section.Exists())
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
                ;
            }
            #endregion

            #region if service_type is not db_query, return 500
            if (HttpContext.Items.ContainsKey("service_type")
                && HttpContext.Items["service_type"] as string != "db_query")
            {
                return StatusCode(500,
                        new
                        {
                            success = false,
                            message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        });
            }
            #endregion

            #region check if the query is empty, return 500 

            var query = section.GetValue<string>("query");

            if (string.IsNullOrWhiteSpace(query))
            {
                return StatusCode(500,
                    new
                    {
                        success = false,
                        message = $"No query defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    });
            }

            #endregion

            #region get parameters
            var qParams = HttpContext.Items["parameters"] as List<DbQueryParams>;
            // If the parameters are not available, then there is a misconfiguration in the middleware chain
            // as even if the request does not have any parameters, the middleware chain should
            // have provided a default set of parameters for each parameter category (i.e., route, query string, body, headers)
            if (qParams == null ||
                qParams.Count < 1)
            {
                return StatusCode(500,
                    new
                    {
                        success = false,
                        message = $"No default parameters defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    });
            }
            #endregion


            #region resolve DbConnection from request scope
            // If the connection is not provided, use the default connection from the settings
            DbConnection connection = _connection;
            // See if the section has a connection string name defined, if so, use it to get the connection string from the configuration
            // and override the default connection
            var connectionStringName = section.GetSection("connection_string_name")?.Value;
            if (!string.IsNullOrWhiteSpace(connectionStringName))
            {
                var connectionString = _configuration.GetConnectionString(connectionStringName);
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    return StatusCode(500,
                        new
                        {
                            success = false,
                            message = $"Connection string `{connectionStringName}` is not defined in the configuration."
                        });
                }
                // If the connection string is the same as the default connection string,
                // then use the default connection, otherwise create a new connection
                if (_connection.ConnectionString != connectionString)
                    // todo: consider using a connection pool here
                    // also consider detecting the type of the connection string
                    // i.e., if it is a SQL Server connection string, then use SqlConnection
                    // if it is a MySQL connection string, then use MySqlConnection, etc.
                    connection = new SqlConnection(connectionString);

            }
            #endregion


            #region get the data from DB and return it
            try
            {
                var response = await _settings.CacheService
                    .Get<ObjectResult>(
                    section,
                    qParams,
                    disableDiffered => GetResultFromDbAsync(section, connection, query, qParams, disableDiffered),
                    HttpContext.RequestAborted
                    );
                return response;

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
                if (_settings.IsDebugMode(Request))
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

                return BadRequest(new { success = false, message = _settings.GetDefaultGenericErrorMessage() });

            }

            #endregion

        }

        /// <summary>
        /// Executes a database query and returns the result as an <see cref="ObjectResult"/>.
        /// </summary>
        /// <param name="serviceQuerySection">The configuration section for the specific service query.</param>
        /// <param name="query">The SQL query to be executed.</param>
        /// <param name="qParams">A list of query parameters to be used in the query.</param>
        /// <param name="disableDifferredExecution">For caching purposes, retrieves all records in memory if enabled so they could be placed in a cache mechanism</param>
        /// <returns>An <see cref="ObjectResult"/> containing the result of the query execution.</returns>
        public async Task<ObjectResult> GetResultFromDbAsync(
            IConfigurationSection serviceQuerySection,
            DbConnection connection,
            string query,
            List<DbQueryParams> qParams,
            bool disableDifferredExecution = false
            )
        {
            int? dbCommandTimeout =
                serviceQuerySection.GetValue<int?>("db_command_timeout") ??
                this._configuration.GetValue<int?>("default_db_command_timeout");

            // check if count query is defined
            var countQuery = serviceQuerySection.GetSection("count_query")?.Value;

            var customSuccessStatusCode = serviceQuerySection.GetValue<int?>("success_status_code") ??
                this._configuration.GetValue<int?>("default_success_status_code") ?? 200;

            // root node name for wrapping the result (if configured - helps with legacy APIs that wraps results within an object)
            // this is experimential and may be removed in future releases in favor of 
            // giving users more control over response structure by defining custom json templates
            // and identifying where the results should be placed within the template
            // for now we just support a single root node name for wrapping the result set
            // This feature will be left undocumented in readme.md for now until the template feature is implemented
            // to be used right now for only specific legacy use cases
            string? rootNodeName = serviceQuerySection.GetValue<string?>("root_node")
                ?? this._configuration.GetValue<string?>("default_root_node") ?? null;


            if (string.IsNullOrWhiteSpace(countQuery))
            {
                var responseStructure = serviceQuerySection.GetValue<string>("response_structure")?.ToLower() ??
                    this._configuration.GetValue<string>("default_response_structure")?.ToLower() ?? "auto";

                // check if `response_structure` is valid (valid values are `array`, `single`, `auto`)
                if (responseStructure != "array" && responseStructure != "single" && responseStructure != "auto")
                {
                    return StatusCode(500, new
                    {
                        success = false,
                        message = $"Invalid response structure `{responseStructure}` defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    });
                }
                var resultWithNoCount = await connection.ExecuteQueryAsync(query, qParams, commandTimeout: dbCommandTimeout, cToken: HttpContext.RequestAborted);
                HttpContext.RequestAborted.ThrowIfCancellationRequested();


                if (responseStructure == "array")
                {

                    if (disableDifferredExecution)
                    {
                        return StatusCode(customSuccessStatusCode,
                            resultWithNoCount.AsEnumerable().ToArray());
                    }
                    if (!string.IsNullOrWhiteSpace(rootNodeName))
                    {
                        // wrap the result in an object with the root node name
                        var wrappedResult = new ExpandoObject();
                        wrappedResult.TryAdd(rootNodeName, resultWithNoCount);
                        return StatusCode(customSuccessStatusCode, resultWithNoCount);
                    }
                    return StatusCode(customSuccessStatusCode, resultWithNoCount);

                }
                if (responseStructure == "single")
                {
                    // if response structure is single, then return the first record


                    var singleResult = resultWithNoCount.AsEnumerable().FirstOrDefault();
                    // close the reader
                    await resultWithNoCount.CloseReaderAsync();
                    if (!string.IsNullOrWhiteSpace(rootNodeName))
                    {
                        // wrap the result in an object with the root node name
                        var wrappedResult = new ExpandoObject();
                        wrappedResult.TryAdd(rootNodeName, (object?) singleResult);
                        return StatusCode(customSuccessStatusCode, wrappedResult);
                    }
                    return StatusCode(customSuccessStatusCode, singleResult);


                }
                if (responseStructure == "auto")
                {
                    // if response structure is auto, then return an array if there are more than one record
                    // and a single record if there is only one record
                    // ToChamberedAsyncEnumerable is a custom extension method that returns an enumerable that have 
                    // some of its items already read into memory. In the case below, the extension method is
                    // instructed to read 2 items into memory and keep the remaining (if any) in the enumerable.
                    // The returned enumerable from the extension method should have a `ChamberedCount` property
                    // matching that of the items count its instructed to read into memory.
                    // If the `ChamberedCount` is less than 2, then this indicates that there is only one (or zero) record
                    // available in the enumerable (i.e., the enumerable is exhausted, in other words ran out of items to iterate through
                    // before it got to our `ChamberedCount` limit).
                    // In this case, we return the first record if it exists, or an empty resultWithNoCount.

                    var chamberedResult = await resultWithNoCount.ToChamberedEnumerableAsync(2, HttpContext.RequestAborted);

                    HttpContext.RequestAborted.ThrowIfCancellationRequested();

                    if (chamberedResult.WasExhausted(2))
                    {
                        var singleResult = chamberedResult.AsEnumerable().FirstOrDefault();
                        // close the reader
                        await resultWithNoCount.CloseReaderAsync();
                        if (!string.IsNullOrWhiteSpace(rootNodeName))
                        {
                            // wrap the result in an object with the root node name
                            var wrappedResult = new ExpandoObject();
                            wrappedResult.TryAdd(rootNodeName, (object?)singleResult);
                            return StatusCode(customSuccessStatusCode, wrappedResult);
                        }

                        return StatusCode(customSuccessStatusCode, singleResult);
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(rootNodeName))
                        {
                            // wrap the result in an object with the root node name
                            var wrappedResult = new ExpandoObject();
                            wrappedResult.TryAdd(rootNodeName,
                                disableDifferredExecution ?
                                chamberedResult.AsEnumerable().ToArray()
                                :
                                chamberedResult);
                            return StatusCode(customSuccessStatusCode, wrappedResult);
                        }

                        return StatusCode(customSuccessStatusCode,
                            disableDifferredExecution ?
                            chamberedResult.AsEnumerable().ToArray()
                            :
                            chamberedResult);
                    }
                }
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Invalid response structure `{responseStructure}` defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }

            var resultCount = await connection.ExecuteQueryAsync(countQuery, qParams, commandTimeout: dbCommandTimeout, cToken: HttpContext.RequestAborted);
            var rowCount = resultCount.AsEnumerable().FirstOrDefault();
            HttpContext.RequestAborted.ThrowIfCancellationRequested();

            if (rowCount == null)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Count query `{countQuery}` did not return any records for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }
            // close the reader for the count query
            await resultCount.CloseReaderAsync();


            var result = await connection.ExecuteQueryAsync(query, qParams, commandTimeout: dbCommandTimeout, cToken: HttpContext.RequestAborted);
            HttpContext.RequestAborted.ThrowIfCancellationRequested();

            if (disableDifferredExecution)
            {

                if (!string.IsNullOrWhiteSpace(rootNodeName))
                {
                    // wrap the result in an object with the root node name
                    var wrappedResult = new ExpandoObject();
                    wrappedResult.TryAdd(rootNodeName,
                        new
                        {
                            success = true,
                            count = rowCount,
                            data = result.AsEnumerable().ToArray()
                        }
                        );
                    return StatusCode(customSuccessStatusCode, wrappedResult);
                }

                // if disableDifferredExecution is true, then we want to read all records into memory
                // so that we can cache them
                return StatusCode(customSuccessStatusCode,
                    new
                    {
                        success = true,
                        count = rowCount,
                        data = result.AsEnumerable().ToArray()
                    });
            }

            if (!string.IsNullOrWhiteSpace(rootNodeName))
            {
                // wrap the result in an object with the root node name
                var wrappedResult = new ExpandoObject();
                wrappedResult.TryAdd(rootNodeName,
                    new
                    {
                        success = true,
                        count = rowCount,
                        data = await result.ToChamberedEnumerableAsync()
                    }
                    );
                return StatusCode(customSuccessStatusCode, wrappedResult);
            }


            return StatusCode(customSuccessStatusCode,
                new
                {
                    success = true,
                    count = rowCount,
                    data = await result.ToChamberedEnumerableAsync()
                });
        }


    }
}
