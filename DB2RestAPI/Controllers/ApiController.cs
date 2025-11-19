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
using Com.H.IO;
using System.ComponentModel.DataAnnotations;

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
        private static readonly HashSet<string> _responseStructures = new HashSet<string>
        {
            "array",
            "single",
            "auto",
            "file"
        };



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

                // Register manually-created connection for disposal
                HttpContext.Response.RegisterForDisposeAsync(connection);

            }
            #endregion


            #region get the data from DB and return it
            try
            {
                var response = await _settings.CacheService
                    .GetQueryResultAsync<IActionResult>(
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
        public async Task<IActionResult> GetResultFromDbAsync(
            IConfigurationSection serviceQuerySection,
            DbConnection connection,
            string query,
            List<DbQueryParams> qParams,
            bool disableDifferredExecution = false
            )
        {
            int? dbCommandTimeout =
                serviceQuerySection.GetValue<int?>("db_command_timeout") ??
                this._configuration.GetValue<int?>("db_command_timeout");

            // check if count query is defined
            var countQuery = serviceQuerySection.GetSection("count_query")?.Value;

            var customSuccessStatusCode = serviceQuerySection.GetValue<int?>("success_status_code") ??
                this._configuration.GetValue<int?>("success_status_code") ?? 200;

            // root node name for wrapping the result (if configured - helps with legacy APIs that wraps results within an object)
            // this is experimential and may be removed in future releases in favor of 
            // giving users more control over response structure by defining custom json templates
            // and identifying where the results should be placed within the template
            // for now we just support a single root node name for wrapping the result set
            // This feature will be left undocumented in readme.md for now until the template feature is implemented
            // to be used right now for only specific legacy use cases
            string? rootNodeName = serviceQuerySection.GetValue<string?>("root_node")
                ?? this._configuration.GetValue<string?>("root_node") ?? null;


            if (string.IsNullOrWhiteSpace(countQuery))
            {
                var responseStructure = serviceQuerySection.GetValue<string>("response_structure")?.ToLower() ??
                    this._configuration.GetValue<string>("response_structure")?.ToLower() ?? "auto";

                // check if `response_structure` is valid (valid values are `array`, `single`, `auto`, `file`)
                if (!_responseStructures.Contains(responseStructure, StringComparer.OrdinalIgnoreCase))
                {
                    return StatusCode(500, new
                    {
                        success = false,
                        message = $"Invalid response structure `{responseStructure}` defined for route "
                        + $"`{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    });
                }
                var resultWithNoCount = await connection.ExecuteQueryAsync(query, qParams, commandTimeout: dbCommandTimeout, cToken: HttpContext.RequestAborted);
                // perhaps here is the right place to register for disposal
                if (resultWithNoCount != null)
                {
                    HttpContext.Response.RegisterForDisposeAsync(resultWithNoCount);
                }

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
                        wrappedResult.TryAdd(rootNodeName, (object?)singleResult);
                        return StatusCode(customSuccessStatusCode, wrappedResult);
                    }
                    return StatusCode(customSuccessStatusCode, singleResult);
                }

                if (responseStructure == "file")
                {
                    return await ReturnFile(resultWithNoCount);
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
            // Register for disposal to ensure DbDataReader is properly cleaned up
            if (resultCount != null)
            {
                HttpContext.Response.RegisterForDisposeAsync(resultCount);
            }

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
            // Register for disposal to ensure DbDataReader is properly cleaned up
            if (result != null)
            {
                HttpContext.Response.RegisterForDisposeAsync(result);
            }

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

        private async Task<IActionResult> ReturnFile(
            DbAsyncQueryResult<dynamic>? resultWithNoCount)
        {
            #region getting the file details from the result set
            // if response structure is file, then return the first record and get the file details from it

            var singleResult = resultWithNoCount?.AsEnumerable().FirstOrDefault();

            // close the reader
            if (resultWithNoCount != null)
                await resultWithNoCount.CloseReaderAsync();

            if (singleResult == null)
            {
                // close the reader
                return NotFound(new
                {
                    success = false,
                    message = $"No file record found to download for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }

            var dictResult = singleResult as IDictionary<string, object?>;
            if (dictResult == null)
            {
                // return 404 not found error
                return NotFound(new
                {
                    success = false,
                    message = $"No file record found to download for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }
            #endregion


            // check if any of the following fields are present in the singleResult:
            /*
                file_name,
                base64_content,
                relative_path,
                content_type,
                http
                Note: if multiple fields are provided (e.g., both `base64_content`, `relative_path` and `http`),
                the app will prioritize the file content source in the following order:
                1- `base64_content`
                2- `relative_path`
                3- `http` (just proxy the file from the provided URL)
            */

            #region file name determination
            string fileName = "downloaded_file"; // default name if file_name or relative_path (from which we can infer the file name) was not provided

            if (dictResult.ContainsKey("file_name")
                && dictResult["file_name"] != null
                && !string.IsNullOrWhiteSpace(dictResult["file_name"]?.ToString()))
            {
                fileName = dictResult["file_name"]!.ToString()!;
            }
            else if (dictResult.ContainsKey("relative_path")
                && dictResult["relative_path"] != null
                && !string.IsNullOrWhiteSpace(dictResult["relative_path"]?.ToString()))
            {
                fileName = Path.GetFileName(dictResult["relative_path"]!.ToString()!);
            }

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "downloaded_file";

            #endregion

            #region determine content type
            // if not provided in the result set, try to determine the content type from file extension
            // and if that fails, use application/octet-stream as the default content type
            string mimeType = string.Empty;
            if (dictResult.ContainsKey("mime_type")
                && dictResult["mime_type"] != null
                && !string.IsNullOrWhiteSpace(dictResult["mime_type"]?.ToString()))
            {
                mimeType = dictResult["mime_type"]!.ToString()!;
            }
            if (string.IsNullOrWhiteSpace(mimeType))
            {
                var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(fileName, out mimeType!))
                {
                    mimeType = "application/octet-stream";
                }
            }

            #endregion

            #region base64 content source handling

            
            if (dictResult.ContainsKey("base64_content")
                && dictResult.TryGetValue("base64_content", out object value) && value is string base64Content
                && !string.IsNullOrWhiteSpace(base64Content)
                )
            {
                // var base64Content = dictResult["base64_content"]?.ToString() ?? string.Empty;
                //if (string.IsNullOrWhiteSpace(base64Content))
                //{
                //    return NotFound(new
                //    {
                //        success = false,
                //        message = $"No file content found to download for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                //    });
                //}
                var fileBytes = Convert.FromBase64String(base64Content);
                return File(fileBytes, mimeType, fileName);
            }
            #endregion
            else if (dictResult.ContainsKey("relative_path"))
            {
                var relativePath = dictResult["relative_path"] as string;
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    relativePath = fileName;
                }
                // get IConfigurationSection for local_file_store from context items
                if (HttpContext.Items.ContainsKey("local_file_store_section"))
                {
                    var localStoreSection = HttpContext.Items["local_file_store_section"] as IConfigurationSection;
                    if (localStoreSection != null
                         && localStoreSection.Exists())
                    {
                        var basePath = localStoreSection.GetValue<string>("base_path") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(basePath)) basePath = AppContext.BaseDirectory;
                        if (!string.IsNullOrWhiteSpace(basePath))
                        {
                            relativePath = Path.Combine(basePath, relativePath).UnifyPathSeperator();
                        }
                        if (!System.IO.File.Exists(relativePath))
                        {
                            return NotFound(new
                            {
                                success = false,
                                message = $"File not found at relative path `{relativePath}` for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            });
                        }
                        
                        // Use async file stream with proper buffering
                        var fileStream = new FileStream(relativePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
                        return File(fileStream, mimeType, fileName);
                    }
                }
                if (HttpContext.Items.ContainsKey("sftp_file_store_section"))
                {
                    var sftpStoreSection = HttpContext.Items["sftp_file_store_section"] as IConfigurationSection;
                    if (sftpStoreSection != null
                         && sftpStoreSection.Exists())
                    {
                        var basePath = HttpContext.Items["base_path"] as string;
                        if (string.IsNullOrWhiteSpace(basePath)) basePath = "";
                        else
                        {
                            relativePath = Path.Combine(basePath, relativePath)
                                .UnifyPathSeperator()
                                .Replace("\\", "/");
                        }
                        string host = sftpStoreSection.GetValue<string>("host") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(host))
                        {
                            return NotFound(new
                            {
                                success = false,
                                message = $"SFTP host not defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            });
                        }

                        int port = sftpStoreSection.GetValue<int?>("port") ?? 22;
                        string username = sftpStoreSection.GetValue<string>("username") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(username))
                        {
                            return NotFound(new
                            {
                                success = false,
                                message = $"SFTP username not defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            });
                        }

                        string password = sftpStoreSection.GetValue<string>("password") ?? string.Empty;

                        var sftpClient = new Com.H.Net.Ssh.SFtpClient(
                            host,
                            port,
                            username,
                            password
                            );
                        // register sftpClient for disposal
                        HttpContext.Response.RegisterForDisposeAsync(sftpClient);


                        var stream = await sftpClient.DownloadAsStreamAsync(
                            relativePath,
                            HttpContext.RequestAborted
                            );


                        if (stream == null)
                        {
                            return NotFound(new
                            {
                                success = false,
                                message = $"File not found at relative path `{relativePath}` for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            });
                        }
                        return File(stream, mimeType, fileName);


                    }
                }



            }
            else if (dictResult.ContainsKey("http"))
            {
                var httpUrl = dictResult["http"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(httpUrl)
                    || !Uri.IsWellFormedUriString(httpUrl, UriKind.Absolute))
                {
                    return NotFound(new
                    {
                        success = false,
                        message = $"Invalid HTTP URL `{httpUrl}` for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"

                    });
                }
                
                using (HttpClient httpClient = new HttpClient())
                {
                    var response = await httpClient.GetAsync(httpUrl, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
                    if (!response.IsSuccessStatusCode)
                    {
                        return StatusCode((int)response.StatusCode, new
                        {
                            success = false,
                            message = $"Failed to download file from `{httpUrl}` for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        });
                    }
                    
                    // Stream the content instead of loading into memory
                    var stream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
                    mimeType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                    return File(stream, mimeType, fileName);
                }
            }
            return NotFound(new
            {
                success = false,
                message = $"No valid file content source found to download for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
            });
        }
    }

}
        
