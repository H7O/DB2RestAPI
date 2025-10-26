using Com.H.Data.Common;
using Com.H.IO;
using DB2RestAPI.Settings;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Identity.Client;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.Collections.Specialized.BitVector32;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace DB2RestAPI.Services;

/// <summary>
/// Service responsible for extracting and validating JSON payloads from HTTP requests.
/// Supports both application/json and multipart/form-data content types.
/// </summary>
public class ParametersBuilder
{

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _config;
    private readonly ILogger<ParametersBuilder> _logger;
    // private readonly string _errorCode = "Payload Extractor Error";
    private static readonly JsonWriterOptions _jsonWriterOptions = new() { Indented = false };
    private static readonly JsonDocumentOptions _jsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly FileExtensionContentTypeProvider _mimeTypeProvider;



    public ParametersBuilder(
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<ParametersBuilder> logger
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

    public string? FilesDataFieldNameInQueryIfAny
    {
        get
        {
            var context = Context;
            var section = Section;
            var filesDataFieldName = context.Items.TryGetValue("files_data_field_name", out var value)
                ? value as string ?? string.Empty
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(filesDataFieldName))
                return filesDataFieldName;

            #region get files data field name from the query (if any)

            var filesRegex = section.GetValue<string>("files_variables_regex");
            if (string.IsNullOrWhiteSpace(filesRegex))
                filesRegex = _config.GetValue<string>("regex:default_files_variables_regex");
            if (string.IsNullOrWhiteSpace(filesRegex))
                filesRegex = DefaultRegex.DefaultFilesVariablesPattern;

            var query = section?.GetValue<string>("query");
            filesDataFieldName = Regex
                .Matches(query ?? string.Empty, filesRegex)?
                .FirstOrDefault()?.Value;

            if (!string.IsNullOrWhiteSpace(filesDataFieldName))
                context.Items["files_data_field"] = filesDataFieldName;
            else filesDataFieldName = null;
            return filesDataFieldName;
            #endregion

        }
    }

    public async Task<List<DbQueryParams>?> GetParamsAsync()
    {
        
        
        var section = Section!;
        var context = Context;
        context.Items.TryGetValue("parameters", out var parameters);

        var qParams = parameters as List<DbQueryParams>;

        if (qParams != null)
            return qParams;

        qParams = new List<DbQueryParams>();

        // order of adding to qParams matters
        // as the later added items have higher priority

        #region get headers parameters

        var headersParam = ExtractHeadersParams();
        if (headersParam != null)
            qParams.Add(headersParam);
        #endregion


        Context.Request.EnableBuffering();


        #region json parameters from body
        var jsonParams = await ExtractParamsFromJsonAsync();

        if (jsonParams != null)
            qParams.Add(jsonParams);
        #endregion



        #region form data parameters from multipart/form-data and application/x-www-form-urlencoded

        var multipartFormParams = await ExtractFromMultipartFormAsync();

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

        context.Items["parameters"] = qParams;
        return qParams;

    }


    private DbQueryParams? ExtractHeadersParams()
    {
        var section = Section;
        var context = Context;

        // add headers to qParams
        var headersVarPattern = section.GetValue<string>("headers_variables_regex");
        if (string.IsNullOrWhiteSpace(headersVarPattern))
            headersVarPattern = _config.GetValue<string>("regex:default_headers_variables_regex");
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
    private async Task<DbQueryParams?> ExtractParamsFromJsonAsync()
    {
        var context = this.Context;
        var section = this.Section;
        var contentType = this.ContentType;
        var filesField = this.FilesDataFieldNameInQueryIfAny;


        var jsonVarRegex = Section.GetValue<string>("json_variables_regex");
        if (string.IsNullOrWhiteSpace(jsonVarRegex))
            jsonVarRegex = _config.GetValue<string>("regex:default_json_variables_regex");
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
                //    section?.GetValue<bool?>("file_stores:pass_files_content_to_query")
                //    ?? _config.GetValue<bool?>("file_stores:pass_files_content_to_query")
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
                    /* todo: perhaps you can create the files structure here (in the else clause) to be used by the step 6 of the middlewares (files processor)
                       the structure to be as described in the sql.xml example:
                        ```json
                        [
                            {
                              "id": "generated GUID for the file by the engine, unless passed by the caller",
                              "name": "example.txt",
                              "relative_path": "2025/Oct/22/<some-guid-goes-here>/example.txt",
                              "extension": ".txt",
                              "size": 1234,
                              "content_base64": "SGVsbG8gd29ybGQh..."
                            },
                            ...
                        ]
                        ```
                      better to do that in the files processor middleware though
                      I think we need to have a consistent place where we create that structure
                      perhaps a helper method in the files processor service that can be called from the middleware
                      however, since at the end the meta data of the files needs to be passed to the sql query
                      and they are considered parameters, perhaps it's better to create that structure here.
                      let's create a method here in the ParametersBuilder service that can be called from anywhere.
                      It should take the user's json payload.
                      the user json structure should have at least the `name` field for each uploaded file.
                      and `content_base64` field if the user wants to pass the content.
                      from our side, we can generate the `id`, `relative_path`, `extension`, and `size` fields.
                      then return that structure to be added to the DbQueryParams.
                      Also, any extra fields passed by the user should be preserved.
                      And the user should have the ability to decide the name of the fields `name` and `content_base64`
                      So add configuration options for that too.
                    */

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




    private string GetMimeTypeFromFileName(string fileName)
    {
        if (_mimeTypeProvider.TryGetContentType(fileName, out var contentType))
        {
            return contentType;
        }
        return "application/octet-stream"; // default mime type
    }



    /// <summary>
    /// Takes the json array string of files meta data (and optionally files content) and builds DbQueryParams for it.
    /// </summary>

    public async Task<JsonProperty?> PrepareFilesJsonProperty(
        JsonProperty jsonArray)
    {
        // check if jsonArray is indeed an array, if not throw exception
        if (jsonArray.Value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"Invalid JSON format: Property `{jsonArray.Name}` must be an array");

        // check if array is empty, if so return null
        if (jsonArray.Value.GetArrayLength() == 0)
            return null;

        var context = this.Context;
        var section = this.Section;

        // get `filename_field_in_payload` from section or config or use default
        var fileNameField = section.GetValue<string>("file_stores:filename_field_in_payload");
        if (string.IsNullOrWhiteSpace(fileNameField))
            fileNameField = _config.GetValue<string>("file_stores:default_filename_field_in_payload");
        if (string.IsNullOrWhiteSpace(fileNameField))
            fileNameField = "name";

        
        // get `base64_content_field_in_payload` from section or config or use default
        var fileContentField = section.GetValue<string>("file_store:base64_content_field_in_payload");
        if (string.IsNullOrWhiteSpace(fileContentField))
            fileContentField = _config.GetValue<string>("file_store:default_base64_content_field_in_payload");
        if (string.IsNullOrWhiteSpace(fileContentField))
            fileContentField = "content_base64";

        // get `relative_file_path_structure` from section or config or use default (which is `{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}`)
        var relativeFilePathStructure = section.GetValue<string>("file_store:relative_file_path_structure");
        if (string.IsNullOrWhiteSpace(relativeFilePathStructure))
            relativeFilePathStructure = _config.GetValue<string>("file_store:default_relative_file_path_structure");
        if (string.IsNullOrWhiteSpace(relativeFilePathStructure))
            relativeFilePathStructure = "{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}";


        // get `max_number_of_files` from section or config or use default (which is unlimited, i.e., null)
        var maxNumberOfFiles = section.GetValue<int?>("file_store:max_number_of_files");
        if (maxNumberOfFiles == null || maxNumberOfFiles < 1)
            maxNumberOfFiles = _config.GetValue<int?>("file_store:default_max_number_of_files") ?? null;

        // get `max_file_size_in_bytes` from section or config or use default (which is unlimited, i.e., null)
        var maxFileSizeInBytes = section.GetValue<long?>("file_store:max_file_size_in_bytes");
        if (maxFileSizeInBytes == null || maxFileSizeInBytes < 1)
            maxFileSizeInBytes = _config.GetValue<long?>("file_store:default_max_file_size_in_bytes") ?? null;

        // get `pass_files_content_to_query` from section or config or use default (which is false)
        var passFilesContentToQuery = section.GetValue<bool?>("file_store:pass_files_content_to_query")??
            _config.GetValue<bool?>("file_store:default_pass_files_content_to_query") ?? false;

        // iterate over each file in the array and build the new array with extra fields namely:
        // id, relative_path, extension, size, mime_type, local_temp_path (if content_base64 is passed)
        foreach (var fileElement in jsonArray.Value.EnumerateArray())
        {
            if (fileElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Invalid JSON format: Each file entry must be a JSON object");
            var fileObject = fileElement;
            // get the file name
            if (!fileObject.TryGetProperty(fileNameField, out var fileNameProperty)
                || fileNameProperty.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(fileNameProperty.GetString()))
            {
                throw new ArgumentException($"Invalid JSON format: Each file object must contain a non-empty string property `{fileNameField}` representing the file name");
            }
            var fileName = fileNameProperty.GetString()!;

            // get `permitted_file_extensions` from section or config or use default (which is all files, i.e., null)
            var permittedFileExtensions = section.GetValue<string>("file_stores:permitted_file_extensions");
            if (string.IsNullOrWhiteSpace(permittedFileExtensions))
                permittedFileExtensions = _config.GetValue<string>("file_stores:default_permitted_file_extensions") ?? null;

            var permittedExtensionsHashSet = permittedFileExtensions?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();

            fileName = ValidateAndGetNormalizeFileName(fileName, permittedExtensionsHashSet);


            // get mime type
            var mimeType = GetMimeTypeFromFileName(fileName);
            // see if user passed ID and check if it's a valid GUID
            Guid fileId;
            if (fileObject.TryGetProperty("id", out var idProperty)
                && idProperty.ValueKind == JsonValueKind.String
                && Guid.TryParse(idProperty.GetString(), out var parsedGuid))
            {
                fileId = parsedGuid;
            }
            else
            {
                fileId = Guid.NewGuid();
            }

            var relativePath = BuildRelativeFilePath(
                relativeFilePathStructure,
                fileName);

            if (!passFilesContentToQuery)
            {
                // async write the content to a temp file and get the size and temp path
                var tempPath = Path.GetTempFileName();
                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (!fileObject.TryGetProperty(fileContentField, out var contentProperty)
                        || contentProperty.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(contentProperty.GetString()))
                    {
                        throw new ArgumentException($"Invalid JSON format: Each file object must contain a non-empty string property `{fileContentField}` representing the base64 content of the file when `pass_files_content_to_query` is false");
                    }
                    var base64Content = contentProperty.GetString()!;
                    byte[] fileBytes;
                    try
                    {
                        fileBytes = Convert.FromBase64String(base64Content);
                    }
                    catch (FormatException)
                    {
                        throw new ArgumentException($"Invalid base64 content in property `{fileContentField}` for file `{fileName}`");
                    }
                    if (maxFileSizeInBytes != null && fileBytes.Length > maxFileSizeInBytes)
                    {
                        throw new ArgumentException($"File `{fileName}` exceeds the maximum allowed size of {maxFileSizeInBytes} bytes");
                    }
                    await fs.WriteAsync(fileBytes, context.RequestAborted);
                }
                // yield return the new file object without content_base64
                yield return new JsonProperty(
                    jsonArray.Name,
                    JsonDocument.Parse($@"
                    {{
                        ""id"": ""{fileId}"",
                        ""name"": ""{fileName}"",
                        ""relative_path"": ""{relativePath}"",
                        ""extension"": ""{Path.GetExtension(fileName)}"",
                        ""size"": 0,
                        ""mime_type"": ""{mimeType}""
                    }}").RootElement);
            }
            else
            {
                // get content_base64
                if (!fileObject.TryGetProperty(fileContentField, out var contentProperty)
                    || contentProperty.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(contentProperty.GetString()))
                {
                    throw new ArgumentException($"Invalid JSON format: Each file object must contain a non-empty string property `{fileContentField}` representing the base64 content of the file when `pass_files_content_to_query` is true");
                }
                var base64Content = contentProperty.GetString()!;
                // decode base64 to get size
                byte[] fileBytes;
                try
                {
                    fileBytes = Convert.FromBase64String(base64Content);
                }
                catch (FormatException)
                {
                    throw new ArgumentException($"Invalid base64 content in property `{fileContentField}` for file `{fileName}`");
                }
                var fileSize = fileBytes.Length;
                // yield return the new file object with content_base64
                yield return new JsonProperty(
                    jsonArray.Name,
                    JsonDocument.Parse($@"
                    {{
                        ""id"": ""{fileId}"",
                        ""name"": ""{fileName}"",
                        ""relative_path"": ""{relativePath}"",
                        ""extension"": ""{Path.GetExtension(fileName)}"",
                        ""size"": {fileSize},
                        ""mime_type"": ""{mimeType}"",
                        ""{fileContentField}"": ""{base64Content}""
                    }}").RootElement);

            }

    }

    public string BuildRelativeFilePath(
        string structure,
        string fileName)
    {
        var now = DateTime.UtcNow;
        var guid = Guid.NewGuid().ToString();
        var relativePath = structure
            .Replace("{date{yyyy}}", now.ToString("yyyy"))
            .Replace("{date{MMM}}", now.ToString("MMM"))
            .Replace("{date{dd}}", now.ToString("dd"))
            .Replace("{{guid}}", guid)
            .Replace("{file{name}}", fileName).UnifyPathSeperator();
        return relativePath;
    }



    #region file name checks and validation
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

    // Check for zero-width and other invisible Unicode characters
    // These can be used to bypass validation or hide malicious content
    private static readonly HashSet<char> InvisibleChars = [
    '\u200B', // Zero-width space
        '\u200C', // Zero-width non-joiner
        '\u200D', // Zero-width joiner  
        '\u200E', // Left-to-right mark
        '\u200F', // Right-to-left mark
        '\uFEFF'  // Zero-width no-break space (BOM)
    ];


    /// <summary>
    /// Validates a user-provided file name for security and compatibility issues.
    /// Prevents path traversal, invalid characters, reserved names, and ensures
    /// the file name is within safe length and character limits. Returns a
    /// normalized version of the file name.
    /// </summary>
    /// <param name="fileName">The user-provided file name to validate (not a path).</param>
    /// <param name="permittedFileExtensions">Extension whitelist (including the dot, e.g., ".txt"). If null or empty, all extensions are allowed.</param>
    /// <exception cref="ArgumentException">Thrown when the file name is invalid.</exception>
    /// <exception cref="SecurityException">Thrown when the file name could escape the base directory.</exception>
    /// <returns>Normalized file name</returns>
    /// <remarks>
    /// IMPORTANT: Extension whitelist configuration must include the dot prefix (e.g., ".txt", ".pdf")
    /// because Path.GetExtension() returns extensions in the format ".ext".
    /// </remarks>

    public static string ValidateAndGetNormalizeFileName(
        string fileName,
        HashSet<string>? permittedFileExtensions = null)
    {


        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name cannot be empty or whitespace.");
        }

        // Normalize to NFC form to prevent Unicode bypass attacks
        if (!fileName.IsNormalized(NormalizationForm.FormC))
        {
            fileName = fileName.Normalize(NormalizationForm.FormC);
        }


        // Check for zero - width and other invisible Unicode characters
        // These can be used to bypass validation or hide malicious content
        if (fileName.Any(c => InvisibleChars.Contains(c)))
        {
            throw new ArgumentException($"File name `{fileName}` contains invisible Unicode characters.");
        }

        // Check for control characters early (includes null bytes)
        // (0x00-0x1F)
        if (fileName.Any(c => char.IsControl(c)))
        {
            throw new ArgumentException($"File name `{fileName}` contains control characters.");
        }



        // Check for NTFS alternate data streams (Windows-specific attack,
        // but won't show up in Path.GetInvalidFileNameChars(), added for cross platform compatiblity
        // in case files were first uploaded to linux then accessed on Windows later, or copied to Windows)
        if (fileName.Contains(':'))
        {
            throw new ArgumentException($"File name `{fileName}` contains colon character (potential alternate data stream).");
        }

        // validate if file name has invalid characters
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            if (fileName.Contains(invalidChar))
            {
                throw new ArgumentException($"File name `{fileName}` contains invalid character `{invalidChar}`");
            }
        }


        // validate if file name is too long
        if (fileName.Length > 150)
        {
            throw new ArgumentException($"File name `{fileName}` is too long. Maximum length is 150 characters.");
        }

        // validate if file name has path traversal characters
        if (fileName.Contains(".."))
        {
            throw new ArgumentException($"File name `{fileName}` contains invalid path traversal sequence `..`");
        }

        // validate if file name has directory separator characters
        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException($"File name `{fileName}` contains invalid directory separator characters.");
        }

        // Check if the base filename (without extension) is reserved
        // although this is Windows specific, it's better to avoid using these names
        // in case the files are ever accessed on a Windows system (such as a Windows-based file share)
        // or copied to a Windows system
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (WindowsReservedNames.Contains(fileNameWithoutExtension))
        {
            throw new ArgumentException($"File name `{fileName}` uses a reserved Windows device name.");
        }

        // Trim and check for changes (also Windows specific, but good practice to have it on linux too for the same reasons as above)
        var trimmedFileName = fileName.Trim(' ', '.');
        if (trimmedFileName != fileName)
        {
            throw new ArgumentException($"File name cannot start or end with spaces or dots.");
        }

        // Check for files that are only dots (Windows restriction)
        if (fileName.All(c => c == '.'))
        {
            throw new ArgumentException($"File name cannot consist only of dots.");
        }


        // Check for leading hyphen (can cause issues with command-line tools)
        if (fileName.StartsWith("-"))
        {
            throw new ArgumentException($"File name cannot start with a hyphen.");
        }

        // Optional: Check for multiple extensions (uncomment if needed)
        // if (fileName.Count(c => c == '.') > 1)
        // {
        //     throw new ArgumentException($"File name `{fileName}` contains multiple extensions.");
        // }


        // base path hasn't yet been decided (to be decided in the next middleware), but for security validation purposes
        // we can assume a base path and check if the combined path escapes it
        var testBasePath = Path.GetTempPath();
        var testFullPath = Path.GetFullPath(Path.Combine(testBasePath, fileName));

        // Ensure the resolved path is within the base directory
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;


        if (!testFullPath.StartsWith(testBasePath, comparison))
        {
            throw new SecurityException($"File path escapes the base directory.");
        }


        if (permittedFileExtensions != null && permittedFileExtensions.Count > 0)
        {
            var fileExtension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(fileExtension))
            {
                throw new ArgumentException("File must have an extension.");
            }

            if (!permittedFileExtensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"File extension `{fileExtension}` is not permitted.");
            }
        }
        return fileName;
    }

    #endregion


    private async Task<DbQueryParams> ExtractFromMultipartFormAsync()
    {
        var context = this.Context;
        var section = this.Section;
        var contentType = this.ContentType;
        var filesField = this.FilesDataFieldNameInQueryIfAny;

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

