using Com.H.Data.Common;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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

        public static List<QueryParams> GetParams(
            this IConfigurationSection serviceQuerySection,
            IConfiguration configuration,
            HttpRequest request,
            JsonElement? payload
            )
        {
            #region prepare query parameters
            var qParams = new List<QueryParams>();

            #region headers
            // add headers to qParams
            var headersRegex = serviceQuerySection.GetSection("headers_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(headersRegex))
                headersRegex = configuration.GetSection("default_headers_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(headersRegex))
                headersRegex = DefaultRegex.DefaultHeadersPattern;

            if (request.Headers?.Count > 0 == true)
            {
                qParams.Add(new QueryParams()
                {
                    DataModel = request.Headers
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
                queryStringRegex = configuration.GetSection("default_query_string_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(queryStringRegex))
                queryStringRegex = DefaultRegex.DefaultQueryStringPattern;

            if (request.Query?.Count > 0 == true)
            {
                qParams.Add(new QueryParams()
                {
                    DataModel = request
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
                varRegex = configuration.GetSection("default_variables_regex")?.Value;
            if (string.IsNullOrWhiteSpace(varRegex))
                varRegex = DefaultRegex.DefaultVariablesPattern;

            if (
                payload != null
                &&
                payload.HasValue
                &&
                !payload.Equals(default)
                && ((JsonElement) payload).ValueKind != JsonValueKind.Null
                && ((JsonElement) payload).ValueKind != JsonValueKind.Undefined
                )
            {
                qParams.Add(new QueryParams()
                {
                    DataModel = (JsonElement) payload,
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


    }
}
