using Com.H.Data.Common;
using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using System.Reflection.Metadata.Ecma335;

namespace DB2RestAPI.Middlewares
{
    /// <summary>
    /// route, section, service_type, parameters, and payload should already be available  
    /// in the context.Items when this middleware is called.
    /// `route` is a string representing the route of the request.
    /// `section` is an IConfigurationSection representing the configuration section of the route.
    /// `service_type` is a string representing the type of service. Current supported services 
    /// are `api_gateway` and `db_query`, however `db_query` is the only one that is currently acceptable 
    /// for this step since if the `service_type` is `api_gateway` then 
    /// the request should have been handled by the `Step6APIGatewayProcess` middleware.
    /// `parameters` is a List<Com.H.Data.Common.QueryParams> representing the parameters of the request.
    /// Which includes the query string parameters, the body parameters and headers.
    /// `payload` is a JsonElement representing the body of the request.
    /// </summary>

    public class Step7DBQueryProcess(
        RequestDelegate next,
        SettingsService settings,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<Step6APIGatewayProcess> logger
            )
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly IConfiguration _configuration = configuration;
        private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
        private readonly ILogger<Step6APIGatewayProcess> _logger = logger;

        public async Task InvokeAsync(HttpContext context)
        {

            #region log the time and the middleware name
            this._logger.LogDebug("{time}: in Step7DBQueryProcess middleware",
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
                            message = "Improper service setup. (Contact your service provider support and provide them with error code `SMWSE7`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }
            #endregion

            #region if service_type is not db_query, return 500
            if (context.Items.ContainsKey("service_type")
                && context.Items["service_type"] as string != "db_query")
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Improper service setup. (Contact your service provider support and provide them with error code `SMWSE6`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }
            #endregion

            #region check if the query is empty, return 404 

            var serviceQuery = section.GetSection("query");

            var query = serviceQuery?.Value;

            if (string.IsNullOrWhiteSpace(query))
            {
                var route = context.Items["route"] as string;
                await context.Response.DeferredWriteAsJsonAsync(
                                    new ObjectResult(
                                        new
                                        {
                                            success = false,
                                            message = string.IsNullOrWhiteSpace(route) ?
                                                "Improper route settings" :
                                                $"API `{route}` not found"
                                        }
                                    )
                                    {
                                        StatusCode = 404
                                    }
                                );
                return;
            }

            #endregion

            #region return result

            

            #endregion

        }

        /// <summary>
        /// Executes a database query and returns the result as an <see cref="ObjectResult"/>.
        /// </summary>
        /// <param name="serviceQuerySection">The configuration section for the specific service query.</param>
        /// <param name="query">The SQL query to be executed.</param>
        /// <param name="qParams">A list of query parameters to be used in the query.</param>
        /// <returns>An <see cref="ObjectResult"/> containing the result of the query execution.</returns>
        public async Task<ObjectResult> GetResultFromDb(
            IConfigurationSection serviceQuerySection,
            string query,
            List<QueryParams> qParams,
            HttpContext context,
            bool disableDifferredExecution = false
            )
        {
            int? dbCommandTimeout =
                serviceQuerySection.GetValue<int?>("db_command_timeout") ??
                this._configuration.GetValue<int?>("default_db_command_timeout");

            // check if count query is defined
            var countQuery = serviceQuerySection.GetSection("count_query")?.Value;


            if (string.IsNullOrWhiteSpace(countQuery))
            {
                if (disableDifferredExecution)
                    return await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            this._connection
                            .ExecuteQuery(query, qParams, commandTimeout: dbCommandTimeout).ToArray()
                        )
                        this._connection
                            .ExecuteQuery(query, qParams, commandTimeout: dbCommandTimeout).ToArray()
                    );

                Ok(disableDifferredExecution ?
                    this._connection
                            .ExecuteQuery(query, qParams, commandTimeout: dbCommandTimeout).ToArray()
                    :
                    this._connection
                            .ExecuteQuery(query, qParams, commandTimeout: dbCommandTimeout)
                            .ToChamberedEnumerable());
            }

            return Ok(
                new
                {
                    success = true,
                    count = ((ExpandoObject?)this._connection
                    .ExecuteQuery(countQuery, qParams, commandTimeout: dbCommandTimeout)
                    .ToList()?.FirstOrDefault())?.FirstOrDefault().Value,
                    data = disableDifferredExecution
                        ? this._connection
                        .ExecuteQuery(query, qParams, commandTimeout: dbCommandTimeout)
                        .ToArray()
                        :
                        this._connection
                        .ExecuteQuery(query, qParams, commandTimeout: dbCommandTimeout)
                        .ToChamberedEnumerable()
                });
        }

    }
}
