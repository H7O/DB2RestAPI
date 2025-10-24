using Com.H.Data.Common;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DB2RestAPI.Settings.Extensinos
{
    public static class ParametersExt
    {

        #region mandatory parameters
        public static string[]? GetMandatoryParameters(
            this IConfigurationSection serviceQuerySection)
        {
            var mandatoryParameters = serviceQuerySection
                .GetSection("mandatory_parameters")?.Value?
                .Split(new char[] { ',', ' ', '\n', '\r' },
                               StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return mandatoryParameters;
        }
        public static ObjectResult? GetFailedMandatoryParamsCheckIfAny(
            this IConfiguration serviceQuerySection,
            List<DbQueryParams> qParams,
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
                // return a response with status code 400 (similar to BadRequest)
                return new ObjectResult(new
                {
                    success = false,
                    message = $"Missing mandatory parameters: {string.Join(",", missingMandatoryParams)}"
                })
                {
                    StatusCode = 400
                };

            //return BadRequest(new
            //    {
            //        success = false,
            //        message = $"Missing mandatory parameters: {string.Join(",", missingMandatoryParams)}"
            //    });

            return null;

        }

        #endregion

        // todo: 
        // 1- remove the jsonPayloadString parameter and instead
        // have the method read the payload from the HttpContext.Request.Body here instead of outside
        // 2- process `multipart/form-data` payloads here too instead of outside in PayloadExtractor
        // 3- add support for `application/x-www-form-urlencoded` processing here too
        
        public static List<DbQueryParams> GetParams(
            this IConfigurationSection serviceQuerySection,
            IConfiguration configuration,
            HttpContext context,
            string? jsonPayloadString = null
            )
        {
            #region prepare query parameters
            var qParams = new List<DbQueryParams>();

            // order of adding to qParams matters
            // as the later added items have higher priority

            #region get headers variables
            // add headers to qParams
            var headersVarPattern = serviceQuerySection?.GetSection("headers_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(headersVarPattern))
                headersVarPattern = configuration.GetSection("default_headers_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(headersVarPattern))
                headersVarPattern = DefaultRegex.DefaultHeadersPattern;

            if (context.Request.Headers?.Count > 0 == true)
            {
                qParams.Add(new DbQueryParams()
                {
                    DataModel = context.Request.Headers
                    .ToDictionary(x => x.Key, x => string.Join("|", x.Value.Where(x => !string.IsNullOrEmpty(x)))),
                    QueryParamsRegex = headersVarPattern
                });
            }
            else
            {
                // add custom header with unlikely name to help
                // set header variables in the query to DbNull.Value
                // in the event that headers are not passed
                // and the user has header variables in the query
                // which if left unset will cause an error
                // the reason for that is the Com.H.Data.Common library
                // when executing a query it'll use the DbQueryParams not only
                // to replace variables in the query (e.g., {{name}})
                // with SQL parameterization version (to protect against sql injections)
                // but it also replaces the variables in the query that aren't passed via the API
                // in the payload with DbNull.
                // e.g., if the sql query had a line like:
                // `declare @some_http_header = {header{x-api-key}}`
                // and the API call never passed `x-api-key`
                // without the regex in the below `headersVarPattern` variable
                // Com.H.Data.Common library won't know which variables left empty to replace them with DbNull
                // when it does it's parameterized version of the line `declare @some_http_header = {header{x-api-key}}`
                // leaving it as is which causes sql exception when running the query.
                // however, if the qParams we're building had an entry with the regex in `headersVarPattern`
                // the Com.H.Data.Common library would do convert that line to a parameterized version of it.
                // which would look like `declare @some_http_header = @x_api_key` then pass DbNull to that `@x_api_key`
                // parameter when running the query.
                qParams.Add(new DbQueryParams()
                {
                    DataModel = new Dictionary<string, string> { { "unlikely_header_to_be_passed", "123" } },
                    QueryParamsRegex = headersVarPattern
                });

            }

            #endregion

            #region get payload variables
            var varRegex = serviceQuerySection?.GetSection("json_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(varRegex))
                varRegex = configuration.GetSection("default_json_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(varRegex))
                varRegex = DefaultRegex.DefaultJsonVariablesPattern;

            if (
                !string.IsNullOrWhiteSpace(jsonPayloadString)
                )
            {
                qParams.Add(new DbQueryParams()
                {
                    DataModel = jsonPayloadString,
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
                // see explanation of `custom headers` above for more details on why this is needed.
                qParams.Add(new DbQueryParams()
                {
                    DataModel = new Dictionary<string, string> { { "unlikely_payload_to_be_passed", "123" } },
                    QueryParamsRegex = varRegex
                });
            }

            #endregion

            #region get query string variables
            // add query string to qParams
            var queryStringRegex = serviceQuerySection?.GetSection("query_string_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(queryStringRegex))
                queryStringRegex = configuration.GetSection("default_query_string_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(queryStringRegex))
                queryStringRegex = DefaultRegex.DefaultQueryStringPattern;

            if (context.Request.Query?.Count > 0 == true)
            {
                qParams.Add(new DbQueryParams()
                {
                    DataModel = context.Request.Query
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
                // see explanation of `custom headers` above for more details on why this is needed.
                qParams.Add(new DbQueryParams()
                {
                    DataModel = new Dictionary<string, string> { { "unlikely_query_string_to_be_passed", "123" } },
                    QueryParamsRegex = queryStringRegex
                });

            }
            #endregion

            #region route variables

            var routeParameterPattern = serviceQuerySection?.GetValue<string>("route_variable_pattern");
            if (string.IsNullOrWhiteSpace(routeParameterPattern))
                routeParameterPattern = serviceQuerySection?.GetValue<string>("default_route_variables_regex");
            if (string.IsNullOrWhiteSpace(routeParameterPattern))
                routeParameterPattern = DefaultRegex.DefaultRouteVariablesPattern;


            if (context.Items["route_parameters"] is Dictionary<string, string> routeParameters && routeParameters?.Count > 0)
            {

                qParams.Add(new DbQueryParams()
                {
                    DataModel = routeParameters,
                    QueryParamsRegex = routeParameterPattern
                });
            }
            else
            {
                // add custom route with unlikely name to help
                // set route variables in the SQL query to DbNull.Value
                // in the event that no route variables were passed
                // and the user has route variables in the SQL query
                // which if left unset will cause an error
                // see explanation of `custom headers` above for more details on why this is needed.
                qParams.Add(new DbQueryParams()
                {
                    DataModel = new Dictionary<string, string> { { "unlikely_route_to_be_passed", "123" } },
                    QueryParamsRegex = routeParameterPattern
                });

            }
            #endregion

            return qParams;
            #endregion
        }





    }
}
