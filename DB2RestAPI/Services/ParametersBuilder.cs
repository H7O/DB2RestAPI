using Com.H.Data.Common;
using Com.H.IO;
using DB2RestAPI.Settings;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Identity.Client;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
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
                    if (!property.NameEquals(filesField))
                    {
                        property.WriteTo(writer);
                    }
                    else
                    {
                        await PrepareFilesJson(property, writer);
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


    #region processing files in JSON array

    /// <summary>
    /// Optimized version - writes directly to the provided Utf8JsonWriter
    /// </summary>
    public async Task PrepareFilesJson(
        JsonProperty jsonArray, Utf8JsonWriter writer)
    {
        // check if jsonArray is indeed an array, if not throw exception
        if (jsonArray.Value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"Invalid JSON format: Property `{jsonArray.Name}` must be an array");

        // Check if array is empty, if so just write empty array
        if (jsonArray.Value.GetArrayLength() == 0)
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
            return;
        }

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

        // get `permitted_file_extensions` from section or config or use default (which is all files, i.e., null)
        var permittedFileExtensions = section.GetValue<string>("file_stores:permitted_file_extensions");
        if (string.IsNullOrWhiteSpace(permittedFileExtensions))
            permittedFileExtensions = _config.GetValue<string>("file_stores:default_permitted_file_extensions") ?? null;

        var permittedExtensionsHashSet = permittedFileExtensions?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();

        // get `accept_caller_defined_file_ids` from section or config or use default (which is false)
        var acceptCallerDefinedFileIds = section.GetValue<bool?>("file_store:accept_caller_defined_file_ids") ??
            _config.GetValue<bool?>("file_store:default_accept_caller_defined_file_ids") ?? false;

        // Write array directly to the provided writer
        writer.WriteStartArray();

        int fileCount = 0;
        // iterate over each file in the array and build the new array with extra fields namely:
        // id, relative_path, extension, size, mime_type, local_temp_path (if content_base64 is passed)
        foreach (var fileElement in jsonArray.Value.EnumerateArray())
        {
            if (maxNumberOfFiles.HasValue && fileCount >= maxNumberOfFiles.Value)
                throw new ArgumentException($"Number of files exceeds the maximum allowed limit of {maxNumberOfFiles.Value}");

            if (fileElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Invalid JSON format: Each file entry must be a JSON object");
            await ProcessSingleFileEntry(
                fileElement,
                writer,
                fileNameField,
                fileContentField,
                relativeFilePathStructure,
                maxFileSizeInBytes,
                passFilesContentToQuery,
                acceptCallerDefinedFileIds,
                permittedExtensionsHashSet,
                context.RequestAborted);

            fileCount++;

        }
        writer.WriteEndArray();

    }

    /// <summary>
    /// Process a single file entry with memory-efficient base64 decoding
    /// </summary>
    private async Task ProcessSingleFileEntry(
        JsonElement fileElement,
        Utf8JsonWriter writer,
        string fileNameField,
        string fileContentField,
        string relativeFilePathStructure,
        long? maxFileSizeInBytes,
        bool passFilesContentToQuery,
        bool acceptCallerDefinedFileIds,
        HashSet<string>? permittedExtensionsHashSet,
        CancellationToken cancellationToken)
    {
        // Get file name
        if (!fileElement.TryGetProperty(fileNameField, out var fileNameProperty)
            || fileNameProperty.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(fileNameProperty.GetString()))
        {
            throw new ArgumentException($"Invalid JSON format: Each file object must contain a non-empty string property `{fileNameField}` representing the file name");
        }

        var fileName = fileNameProperty.GetString()!;
        fileName = ValidateAndGetNormalizeFileName(fileName, permittedExtensionsHashSet);

        // Get or generate file ID
        Guid fileId;
        if (acceptCallerDefinedFileIds
            && fileElement.TryGetProperty("id", out var idProperty)
            && idProperty.ValueKind == JsonValueKind.String
            && Guid.TryParse(idProperty.GetString(), out var parsedGuid))
        {
            fileId = parsedGuid;
        }
        else
        {
            fileId = Guid.NewGuid();
        }

        var relativePath = BuildRelativeFilePath(relativeFilePathStructure, fileName);
        var mimeType = GetMimeTypeFromFileName(fileName);

        // Get base64 content
        if (!fileElement.TryGetProperty(fileContentField, out var contentProperty)
            || contentProperty.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(contentProperty.GetString()))
        {
            throw new ArgumentException($"Invalid JSON format: Each file object must contain a non-empty string property `{fileContentField}` representing the base64 content of the file");
        }

        var base64Content = contentProperty.GetString()!;

        // Write file object directly to writer
        writer.WriteStartObject();
        writer.WriteString("id", fileId);
        writer.WriteString(fileNameField, fileName);
        writer.WriteString("relative_path", relativePath);
        writer.WriteString("extension", Path.GetExtension(fileName));
        writer.WriteString("mime_type", mimeType);

        if (!passFilesContentToQuery)
        {
            // Write base64 content to temp file with streaming decode
            var (tempPath, fileSize) = await WriteBase64ToTempFileStreaming(
                base64Content,
                maxFileSizeInBytes,
                fileName,
                cancellationToken);

            writer.WriteNumber("size", fileSize);
            writer.WriteString("backend_base64_temp_file_path", tempPath);
            // Don't write the base64 content
        }
        else
        {
            // Decode to get size but keep content in JSON
            var fileSize = GetBase64DecodedSize(base64Content);

            if (maxFileSizeInBytes.HasValue && fileSize > maxFileSizeInBytes.Value)
            {
                throw new ArgumentException($"File `{fileName}` exceeds the maximum allowed size of {maxFileSizeInBytes.Value} bytes");
            }

            writer.WriteNumber("size", fileSize);
            writer.WriteString(fileContentField, base64Content);
        }

        // Copy any additional properties from original file object
        foreach (var prop in fileElement.EnumerateObject())
        {
            // Skip properties we've already written
            if (prop.Name == fileNameField ||
                prop.Name == fileContentField ||
                prop.Name == "id")
                continue;

            writer.WritePropertyName(prop.Name);
            prop.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }



    /// <summary>
    /// Memory-efficient streaming base64 decode and write to temp file
    /// Uses ArrayPool for buffer management and FromBase64Transform for chunked decoding
    /// </summary>
    private async Task<(string tempPath, long fileSize)> WriteBase64ToTempFileStreaming(
        string base64Content,
        long? maxFileSizeInBytes,
        string fileName,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.GetTempFileName();
        long totalBytesWritten = 0;

        try
        {
            await using var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            using var transform = new FromBase64Transform();

            const int chunkSize = 4096; // Must be multiple of 4 for base64
            int offset = 0;

            while (offset < base64Content.Length)
            {
                int length = Math.Min(chunkSize, base64Content.Length - offset);

                // Ensure we're at a valid base64 boundary
                if (offset + length < base64Content.Length && length % 4 != 0)
                {
                    length = (length / 4) * 4;
                }

                if (length == 0)
                    break;

                // Rent buffers from ArrayPool for zero-allocation processing
                byte[] inputBuffer = ArrayPool<byte>.Shared.Rent(length);
                byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(length);

                try
                {
                    int bytesEncoded = Encoding.ASCII.GetBytes(
                        base64Content.AsSpan(offset, length),
                        inputBuffer);

                    bool isFinalBlock = (offset + length >= base64Content.Length);

                    if (isFinalBlock)
                    {
                        byte[] finalOutput = transform.TransformFinalBlock(inputBuffer, 0, bytesEncoded);
                        await fileStream.WriteAsync(finalOutput, cancellationToken);
                        totalBytesWritten += finalOutput.Length;
                    }
                    else
                    {
                        int outputBytes = transform.TransformBlock(
                            inputBuffer, 0, bytesEncoded,
                            outputBuffer, 0);

                        await fileStream.WriteAsync(
                            outputBuffer.AsMemory(0, outputBytes),
                            cancellationToken);

                        totalBytesWritten += outputBytes;
                    }

                    // Check size limit during processing
                    if (maxFileSizeInBytes.HasValue && totalBytesWritten > maxFileSizeInBytes.Value)
                    {
                        throw new ArgumentException($"File `{fileName}` exceeds the maximum allowed size of {maxFileSizeInBytes.Value} bytes");
                    }

                    offset += length;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(inputBuffer);
                    ArrayPool<byte>.Shared.Return(outputBuffer);
                }
            }

            return (tempPath, totalBytesWritten);
        }
        catch
        {
            // Clean up temp file if something goes wrong
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Calculate decoded size without fully decoding (for when content stays in JSON)
    /// </summary>
    private long GetBase64DecodedSize(string base64Content)
    {
        if (string.IsNullOrEmpty(base64Content))
            return 0;

        int padding = 0;
        if (base64Content.EndsWith("=="))
            padding = 2;
        else if (base64Content.EndsWith("="))
            padding = 1;

        return (base64Content.Length * 3L / 4L) - padding;
    }


    #endregion



    #region helpers for processing files in JSON array

    private string GetMimeTypeFromFileName(string fileName)
    {
        if (_mimeTypeProvider.TryGetContentType(fileName, out var contentType))
        {
            return contentType;
        }
        return "application/octet-stream"; // default mime type
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

