using Com.H.Data.Common;
using DB2RestAPI.Settings;
using System.Text;
using System.Text.Json;

namespace DB2RestAPI.Services;

/// <summary>
/// Service responsible for extracting and validating JSON payloads from HTTP requests.
/// Supports both application/json and multipart/form-data content types.
/// </summary>
public class PayloadExtractor
{

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _config;
    private readonly ILogger<PayloadExtractor> _logger;
    private readonly string _errorCode = "Payload Extractor Error";
    private static readonly JsonWriterOptions _jsonWriterOptions = new() { Indented = false };
    private static readonly JsonDocumentOptions _jsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };


    public PayloadExtractor(
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<PayloadExtractor> logger
        )
    {

        _httpContextAccessor = httpContextAccessor;
        _config = configuration;
        _logger = logger;

    }

    private string ContentType
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            return context?.Items.TryGetValue("content_type", out var value) == true
                ? value as string ?? string.Empty
                : string.Empty;
        }
    }

    private IConfigurationSection Section
    {
        get
        {
            if (!Context.Items.TryGetValue("section", out var sectionValue))
                throw new ArgumentNullException("Configuration section not found");

            return sectionValue as IConfigurationSection ?? throw new ArgumentNullException("Invalid section type");
        }
    }

    public HttpContext Context
    {
        get
        {
            return _httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("HttpContext is not available");
        }
    }



    public async Task<List<DbQueryParams>?> GetParamsAsync()
    {
        
        var qParams = new List<DbQueryParams>();
        var section = Section!;
        var context = Context;
        // order of adding to qParams matters
        // as the later added items have higher priority

        #region get headers parameters

        var headersParam = ExtractHeadersParams();
        if (headersParam != null)
            qParams.Add(headersParam);
        #endregion


        #region get files data field from the query (if any)
        var query = section?.GetValue<string>("query");
        var filesDataField = DefaultRegex.DefaultFilesVariablesCompiledRegex.Matches(query ?? string.Empty)?.FirstOrDefault()?.Value;
        if (!string.IsNullOrWhiteSpace(filesDataField))
            context.Items["files_data_field"] = filesDataField;
        #endregion

        Context.Request.EnableBuffering();


        #region json parameters from body
        var jsonParams = await ExtractParamsFromJsonAsync(filesDataField);

        if (jsonParams != null)
            qParams.Add(jsonParams);
        #endregion



        #region form data parameters from multipart/form-data and application/x-www-form-urlencoded

        var multipartFormParams = await ExtractFromMultipartFormAsync(filesDataField);

        if (multipartFormParams != null)
            qParams.Add(multipartFormParams);

        #endregion




        #region get query string variables

        var queryStringParams = ExtractQueryStringParamsAsync();
        if (queryStringParams != null)
            qParams.Add(queryStringParams);
        #endregion


        #region route variables

        var routeParams = ExtractRouteParams();
        if (routeParams != null)
            qParams.Add(routeParams);

        #endregion

        return qParams;

    }


    private DbQueryParams? ExtractHeadersParams()
    {
        var section = Section;
        var context = Context;

        // add headers to qParams
        var headersVarPattern = section.GetValue<string>("headers_variables_regex");
        if (string.IsNullOrWhiteSpace(headersVarPattern))
            headersVarPattern = _config.GetValue<string>("default_headers_variables_regex");
        if (string.IsNullOrWhiteSpace(headersVarPattern))
            headersVarPattern = DefaultRegex.DefaultHeadersPattern;

        if (context.Request.Headers?.Count > 0 == true)
            return new DbQueryParams()
            {
                DataModel = context.Request.Headers
                    .ToDictionary(x => x.Key, x => string.Join("|", x.Value.Where(x => !string.IsNullOrEmpty(x)))),
                QueryParamsRegex = headersVarPattern
            };

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
        return new DbQueryParams()
        {
            DataModel = new Dictionary<string, string> { { "unlikely_header_to_be_passed", "123" } },
            QueryParamsRegex = headersVarPattern
        };
    }


    /// <summary>
    /// Extracts DbQueryparams from JSON payload from application/json request body
    /// </summary>
    private async Task<DbQueryParams?> ExtractParamsFromJsonAsync(string? filesField)
    {
        var context = this.Context;
        var section = this.Section;
        var contentType = this.ContentType;

        
        var jsonVarRegex = Section.GetValue<string>("json_variables_regex");
        if (string.IsNullOrWhiteSpace(jsonVarRegex))
            jsonVarRegex = _config.GetValue<string>("default_json_variables_regex");
        if (string.IsNullOrWhiteSpace(jsonVarRegex))
            jsonVarRegex = DefaultRegex.DefaultJsonVariablesPattern;

        var nullProtectionParams = () => new DbQueryParams()
        {
            DataModel = new Dictionary<string, string> { { "unlikely_header_to_be_passed", "123" } },
            QueryParamsRegex = jsonVarRegex
        };

        if (!contentType.Contains("application/json") == true)
            return nullProtectionParams();


        try
        {

            // Validate and normalize JSON
            using (JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, _jsonDocumentOptions, context.RequestAborted))
            {

                // check if there is a files field to extract
                // if not then return DbQueryParams with the whole JSON payload as is

                // the below should be checked at the next middleware not here, left here as a reminder
                // to do it in the next middleware (remove it once you do that)
                //var passFilesContentToQuery =
                //    section?.GetValue<bool?>("document_management:pass_files_content_to_query")
                //    ?? _config.GetValue<bool?>("document_management:pass_files_content_to_query")
                //    ?? false;

                if (string.IsNullOrWhiteSpace(filesField))
                {
                    return new DbQueryParams()
                    {
                        DataModel = document.RootElement.GetRawText(),
                        QueryParamsRegex = jsonVarRegex
                    };
                }

                using var ms = new MemoryStream();
                using var writer = new Utf8JsonWriter(ms, _jsonWriterOptions);

                JsonElement root = document.RootElement;

                writer.WriteStartObject();

                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (!property.NameEquals(filesField)) // Skip the files property
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
                await writer.FlushAsync(context.RequestAborted);

                return new DbQueryParams()
                {
                    DataModel = Encoding.UTF8.GetString(ms.ToArray()),
                    QueryParamsRegex = jsonVarRegex
                };

            }
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "Invalid JSON in request body");
            return nullProtectionParams();
        }
        finally
        {
            // Reset stream position for next middleware
            context.Request.Body.Position = 0;
        }
    }


    private async Task<DbQueryParams> ExtractFromMultipartFormAsync(string? filesField)
    {
        var context = this.Context;
        var section = this.Section;
        var contentType = this.ContentType;
        
        var formDataVarRegex = section.GetValue<string>("form_data_variables_regex");
        if (string.IsNullOrWhiteSpace(formDataVarRegex))
            formDataVarRegex = _config.GetValue<string>("default_form_data_variables_regex");
        if (string.IsNullOrWhiteSpace(formDataVarRegex))
            formDataVarRegex = DefaultRegex.DefaultFormDataVariablesPattern;
        var nullProtectionParams = () => new DbQueryParams()
        {
            DataModel = new Dictionary<string, string> { { "unlikely_header_to_be_passed", "123" } },
            QueryParamsRegex = formDataVarRegex
        };

        // somehow context.Request.Content is not avaiable here (compiler error - not recognized as a property), need to investigate later
        //if (!context.Request.Content.IsMimeMultipartContent())
        //{
        //    return nullProtectionParams();
        //}

        if (!(
            contentType.Contains("multipart/form-data") == true
            || contentType.Contains("application/x-www-form-urlencoded") == true
            )
            )
            return nullProtectionParams();

        try
        {
            // Read the form data
            var form = await context.Request.ReadFormAsync(context.RequestAborted);

            if (form == null)
                return nullProtectionParams();
            // todo: exclude `filesField` and also exclude files in the multipart form data if any
            return new DbQueryParams()
            {
                DataModel = form.Where(kvp => string.IsNullOrWhiteSpace(filesField)
                || !kvp.Key.Equals(filesField, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kvp => kvp.Key, kvp => string.Join("|", kvp.Value.Where(v => !string.IsNullOrEmpty(v)))
                ),
                QueryParamsRegex = formDataVarRegex
            };
        }
        catch
        {
            return nullProtectionParams();
        }
        finally
        {
            // Reset stream position for next middleware
            context.Request.Body.Position = 0;
        }

    }


    private DbQueryParams ExtractQueryStringParamsAsync()
    {
        var context = this.Context;
        var section = this.Section;
        var contentType = this.ContentType;
        var queryStringVarRegex = section.GetValue<string>("query_string_variables_regex");
        if (string.IsNullOrWhiteSpace(queryStringVarRegex))
            queryStringVarRegex = _config.GetValue<string>("default_query_string_variables_regex");
        if (string.IsNullOrWhiteSpace(queryStringVarRegex))
            queryStringVarRegex = DefaultRegex.DefaultQueryStringPattern;

        if (context.Request.Query?.Count > 0 == true)
        {
            return new DbQueryParams()
            {
                DataModel = context.Request.Query
                .ToDictionary(x => x.Key, x => string.Join("|", x.Value.Where(x => !string.IsNullOrEmpty(x)))),
                QueryParamsRegex = queryStringVarRegex
            };
        }
        else
        {
            return new DbQueryParams()
            {
                DataModel = new Dictionary<string, string> { { "unlikely_header_to_be_passed", "123" } },
                QueryParamsRegex = queryStringVarRegex
            };
        }
    }

    private DbQueryParams ExtractRouteParams()
    {
        var context = this.Context;
        var section = this.Section;
        var contentType = this.ContentType;
        var routeVarRegex = section.GetValue<string>("route_variables_regex");
        if (string.IsNullOrWhiteSpace(routeVarRegex))
            routeVarRegex = _config.GetValue<string>("default_route_variables_regex");
        if (string.IsNullOrWhiteSpace(routeVarRegex))
            routeVarRegex = DefaultRegex.DefaultRouteVariablesPattern;
        if (context.Items["route_parameters"] is Dictionary<string, string> routeParameters && routeParameters?.Count > 0)
        {
            return new DbQueryParams()
            {
                DataModel = routeParameters,
                QueryParamsRegex = routeVarRegex
            };
        }
        else
        {
            // add custom route with unlikely name to help
            // set route variables in the SQL query to DbNull.Value
            // in the event that no route variables were passed
            // and the user has route variables in the SQL query
            // which if left unset will cause an error
            // see explanation of `custom headers` above for more details on why this is needed.
            return new DbQueryParams()
            {
                DataModel = new Dictionary<string, string> { { "unlikely_route_to_be_passed", "123" } },
                QueryParamsRegex = routeVarRegex
            };
        }
    }   


}

