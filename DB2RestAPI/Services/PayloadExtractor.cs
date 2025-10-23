using System.Text.Json;

namespace DB2RestAPI.Services;

/// <summary>
/// Service responsible for extracting and validating JSON payloads from HTTP requests.
/// Supports both application/json and multipart/form-data content types.
/// </summary>
public class PayloadExtractor
{
    /// <summary>
    /// Extracts JSON payload from the HTTP request based on content type.
    /// For application/json: Reads directly from request body
    /// For multipart/form-data: Reads from configured form field (default: "json")
    /// </summary>
    /// <param name="context">The HTTP context containing the request</param>
    /// <param name="contentType">The content type of the request</param>
    /// <param name="section">Configuration section for the route (optional, for custom field names)</param>
    /// <param name="configuration">Global configuration (optional, for default field names)</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>
    /// A tuple containing:
    /// - jsonPayload: The parsed JSON payload string, or null if no valid JSON found
    /// - errorResponse: An error response object if validation failed, or null if successful
    /// </returns>
    public static async Task<(string? jsonPayload, object? errorResponse)> ExtractJsonPayloadAsync(
        HttpContext context,
        string? contentType,
        IConfigurationSection? section = null,
        IConfiguration? configuration = null,
        CancellationToken cancellationToken = default)
    {
        // Enable buffering for the request body so it can be read multiple times
        context.Request.EnableBuffering();

        string? jsonPayloadString = null;

        // Handle application/json content type
        if (contentType?.Contains("application/json") == true)
        {
            jsonPayloadString = await ExtractFromJsonBodyAsync(context, cancellationToken);
            if (jsonPayloadString == "INVALID_JSON")
            {
                return (null, new
                {
                    success = false,
                    message = "Invalid JSON format"
                });
            }
        }
        // Handle multipart/form-data content type
        else if (contentType?.Contains("multipart/form-data") == true)
        {
            var result = await ExtractFromMultipartFormAsync(context, section, configuration, cancellationToken);
            jsonPayloadString = result.jsonPayload;
            if (result.errorResponse != null)
            {
                return (null, result.errorResponse);
            }
        }

        return (jsonPayloadString, null);
    }

    /// <summary>
    /// Extracts JSON payload from application/json request body
    /// </summary>
    private static async Task<string?> ExtractFromJsonBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken);

        // Reset stream position for next middleware
        context.Request.Body.Position = 0;

        if (cancellationToken.IsCancellationRequested)
            return null;

        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            // Validate and normalize JSON
            using (JsonDocument document = JsonDocument.Parse(body))
            {
                return document.RootElement.ToString();
            }
        }
        catch (JsonException)
        {
            return "INVALID_JSON"; // Special marker for invalid JSON
        }
    }

    /// <summary>
    /// Extracts JSON payload from multipart/form-data form field
    /// </summary>
    private static async Task<(string? jsonPayload, object? errorResponse)> ExtractFromMultipartFormAsync(
        HttpContext context,
        IConfigurationSection? section,
        IConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        // Determine the form field name for JSON payload
        // Priority: route setting > document_management setting > default "json"
        var jsonFieldName = section?.GetValue<string>("document_management:json_payload_form_field_name");
        
        if (string.IsNullOrWhiteSpace(jsonFieldName))
        {
            jsonFieldName = configuration?.GetValue<string>("document_management:json_payload_form_field_name");
        }
        
        if (string.IsNullOrWhiteSpace(jsonFieldName))
        {
            jsonFieldName = "json"; // Default field name
        }

        try
        {
            // Read the form data
            var form = await context.Request.ReadFormAsync(cancellationToken);

            // Reset stream position for next middleware
            context.Request.Body.Position = 0;

            if (cancellationToken.IsCancellationRequested)
            {
                return (null, new
                {
                    success = false,
                    message = "Request was cancelled"
                });
            }

            // Check if the JSON field exists in the form
            if (!form.ContainsKey(jsonFieldName))
            {
                // It's optional to have JSON payload in multipart/form-data
                // (e.g., file-only uploads without additional data)
                return (null, null);
            }

            var jsonValue = form[jsonFieldName].ToString();

            if (string.IsNullOrWhiteSpace(jsonValue))
            {
                return (null, null);
            }

            // Validate the JSON
            try
            {
                using (JsonDocument document = JsonDocument.Parse(jsonValue))
                {
                    return (document.RootElement.ToString(), null);
                }
            }
            catch (JsonException)
            {
                return (null, new
                {
                    success = false,
                    message = $"Invalid JSON format in form field '{jsonFieldName}'"
                });
            }
        }
        catch (InvalidOperationException ex)
        {
            // This can happen if the content type is multipart/form-data but the body is not valid
            return (null, new
            {
                success = false,
                message = $"Failed to read multipart form data: {ex.Message}"
            });
        }
    }
}
