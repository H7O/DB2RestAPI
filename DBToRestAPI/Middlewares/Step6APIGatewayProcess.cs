using Azure;
using Azure.Core;
using Com.H.Data;
using DBToRestAPI.Cache;
using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DBToRestAPI.Middlewares
{
    /// <summary>
    /// Fourth middleware in the pipeline that processes API gateway routing requests.
    /// 
    /// This middleware acts as a reverse proxy, forwarding requests to external APIs when the
    /// service type is 'api_gateway'. For other service types (e.g., 'db_query'), it passes 
    /// the request to the next middleware without modification.
    /// 
    /// Required context.Items from previous middlewares:
    /// - `route`: String representing the matched route path
    /// - `section`: IConfigurationSection for the route's configuration
    /// - `service_type`: String indicating service type (must be `api_gateway` for processing)
    /// - `remaining_path`: Additional path segments to append to target URL (for wildcard routes)
    /// 
    /// API Gateway functionality:
    /// - Constructs target URL from route configuration
    /// - Forwards HTTP method, headers, query strings, and request body
    /// - Supports header exclusion and override (per-route or default)
    /// - Handles SSL certificate validation settings
    /// - Streams response back to the caller with appropriate headers
    /// 
    /// Configuration options per route:
    /// - `url`: Target API endpoint (required)
    /// - `excluded_headers`: Headers to exclude when forwarding
    /// - `applied_headers`: Headers to add/override in forwarded request
    /// - `ignore_target_route_certificate_errors`: Allow self-signed/invalid certificates
    /// 
    /// Responses:
    /// - Proxied response: Returns the exact response from the target API
    /// - 500 Internal Server Error: Missing configuration or improper setup
    /// - Passes to next middleware: Service type is not 'api_gateway'
    /// </summary>

    public class Step6APIGatewayProcess(
        RequestDelegate next,
        IConfiguration configuration,
        SettingsService settings,
        IHttpClientFactory httpClientFactory,
        ILogger<Step6APIGatewayProcess> logger,
        ParametersBuilder paramsBuilder
            )
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly IConfiguration _configuration = configuration;
        private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
        private readonly ILogger<Step6APIGatewayProcess> _logger = logger;
        private readonly ParametersBuilder _paramsBuilder = paramsBuilder;
        /// <summary>
        /// Headers that should not be copied from the target response to the client response.
        /// These headers are managed by ASP.NET Core and manually setting them could cause issues.
        /// </summary>
        private static readonly string[] _excludedResponseHeaders = new string[] { "Transfer-Encoding", "Content-Length" };
        private static readonly string _errorCode = "Step 5 - Gateway Process Error";

        // List of headers that belong to Content, not to the request itself
        private static readonly HashSet<string> _contentHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Type", "Content-Length", "Content-Encoding", "Content-Language",
            "Content-Location", "Content-MD5", "Content-Range", "Content-Disposition"
        };



        public async Task InvokeAsync(HttpContext context)
        {

            #region log the time and the middleware name
            this._logger.LogDebug("{time}: in Step4APIGatewayProcess middleware",
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
                            // SMWSE6: standard middleware section error
                            message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );

                return;
            }
            #endregion

            #region if no service type passed from the previous middlewares, return 500


            //var containsServiceType = context.Items.ContainsKey("service_type")
            //    && context.Items["serivce_type"] is string serviceType
            //    && !string.IsNullOrWhiteSpace(serviceType);

            //this._logger.LogDebug("Contains service type: {containsServiceType}",
            //    containsServiceType);

            if (!context.Items.ContainsKey("service_type"))
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            // SMWSTE6: standard middleware service type error 6
                            message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }
            #endregion

            #region if service type is not `api_gateway`, call the next middleware
            if (context.Items["service_type"] as string != "api_gateway")
            {
                await _next(context);
                return;
            }

            #endregion

            #region url check
            var url = section.GetValue<string>("url");

            if (string.IsNullOrWhiteSpace(url))
            {

                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Improper route settings (missing `url`). (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }

            #endregion

            #region get parameters
            // retrieve the parameters (which consists of query string parameters and headers)
            var qParams = await this._paramsBuilder.GetParamsAsync();

            if (qParams == null)
                {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Improper parameters setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }


            #endregion

            #region check if there are any mandatory parameters missing
            var failedMandatoryCheckResponse = this._settings
                .GetFailedMandatoryParamsCheckIfAny(section, qParams);
            if (failedMandatoryCheckResponse != null)
            {
                await context.Response.DeferredWriteAsJsonAsync(failedMandatoryCheckResponse);
                return;
            }
            #endregion


            #region get remaining path and apply it to the url
            var remainingPath = context.Items["remaining_path"] as string;
            if (!string.IsNullOrWhiteSpace(remainingPath))
            {
                // url might have a query string, 
                // so we need to insert the remaining path
                // before the query string or append it to the url
                // if there is no query string.
                if (!url.Contains('?'))
                    url += remainingPath;
                else
                    // insert the remaining path before the query string `?`
                    url = url.Insert(url.IndexOf('?'), remainingPath);
            }
            #endregion

            #region get caller's query string

            // check if `this.Request` has query string
            // if queryString has values, append it to the url, and if the url already has a query string, append it with `&`
            if (!string.IsNullOrWhiteSpace(context.Request.QueryString.Value))
            {
                url += string.Concat(url.Contains('?') ? "&" : "?", context.Request.QueryString.Value.AsSpan(1));
                // the above is equivalent to:
                // url += (url.Contains("?") ? "&" : "?") + context.Request.QueryString.Value.Substring(1);
            }

            #endregion

            #region get resolved route for cache key
            var resolvedRoute = context.Items["route"] as string ?? string.Empty;
            #endregion

            #region cache implementation
            try
            {
                var cachedResponse = await _settings.CacheService.GetForGateway<CachableHttpResponseContainer>(
                    section,
                    context,
                    resolvedRoute,
                    disableStreaming => ProcessApiGatewayRequestAsync(section, context, url, disableStreaming),
                    context.RequestAborted
                );

                // If response is null, it means streaming mode was used and response already written
                if (cachedResponse == null)
                    return;

                // Response came from cache or was buffered - write it to the client
                await WriteHttpResponseFromCache(context, cachedResponse);
            }
            catch (Exception ex)
            {
                await context.Response.DeferredWriteAsJsonAsync(_settings.GetExceptionResponse(context.Request, ex));
            }
            #endregion

        }

        /// <summary>
        /// Processes the API gateway request by forwarding it to the target URL.
        /// </summary>
        /// <param name="section">Configuration section for the route.</param>
        /// <param name="context">The HTTP context.</param>
        /// <param name="url">The target URL to forward the request to.</param>
        /// <param name="disableStreaming">If true, buffers the entire response for caching. If false, streams directly to client.</param>
        /// <returns>CachableHttpResponseContainer if buffered, null if streamed.</returns>
        private async Task<CachableHttpResponseContainer?> ProcessApiGatewayRequestAsync(
            IConfigurationSection section,
            HttpContext context,
            string url,
            bool disableStreaming)
        {
            #region prepare target request msg
            var targetRequestMsg = new HttpRequestMessage(new HttpMethod(context.Request.Method), url);

            // see if the request has a body
            if (context.Request.Body?.CanRead == true)
                targetRequestMsg.Content = new StreamContent(context.Request.Body);

            #endregion

            #region see if there are headers that should not be passed to the server for this particular route
            var excludeHeaders = section.GetValue<string>("excluded_headers")?
                .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (excludeHeaders == null || excludeHeaders.Length < 1)
                // if no headers to exclude for this route, check if there are default headers to exclude for all routes
                excludeHeaders = _configuration.GetValue<string>("excluded_headers")?
                    .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            #endregion

            #region see if there are headers that should be overridden for this particular route

            var appliedHeaders = section.GetSection("applied_headers")?.GetChildren()?
                // remove null `name` headers
                .Where(x => !string.IsNullOrWhiteSpace(x.GetValue<string>("name")))
                .Select(x => new KeyValuePair<string, string>(x.GetValue<string>("name")!,
                x.GetValue<string>("value") ?? string.Empty))
                .ToDictionary(x => x.Key, x => x.Value);
            // adding the override headers to the target request
            if (appliedHeaders?.Count > 0 == true)
            {
                foreach (var header in appliedHeaders)
                {
                    bool success;
                    // Content headers must be added to Content.Headers, not to request Headers
                    if (_contentHeaderNames.Contains(header.Key))
                    {
                        // Only add to Content.Headers if we have content
                        if (targetRequestMsg.Content != null)
                        {
                            success = targetRequestMsg.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            if (!success)
                                this._logger.LogWarning("Failed to add `applied/overridden` content header `{headerKey}` to target request",
                                    header.Key);
                        }
                    }
                    else
                    {
                        success = targetRequestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        if (!success)
                            this._logger.LogWarning("Failed to add `applied/overridden` header `{headerKey}` to target request",
                                header.Key);
                    }
                }
            }

            #endregion

            #region get headers from the caller request and add them to the target request
            foreach (var header in context.Request.Headers)
            {

                if (
                    // exclude headers that should not be passed to the server (make sure to accomodate for case sensitivity)
                    excludeHeaders?.Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
                    || appliedHeaders?.Select(x => x.Key).Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
                    )
                    continue;

                bool success;
                // Content headers must be added to Content.Headers, not to request Headers
                if (_contentHeaderNames.Contains(header.Key))
                {
                    // Only add to Content.Headers if we have content
                    if (targetRequestMsg.Content != null)
                    {
                        success = targetRequestMsg.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                        if (!success)
                            this._logger.LogWarning("Failed to add content header `{headerKey}` to target request",
                                header.Key);
                    }
                }
                else
                {
                    success = targetRequestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    if (!success)
                        this._logger.LogWarning("Failed to add header `{headerKey}` to target request",
                            header.Key);
                }
            }
            #endregion

            #region check if the target route certificate errors should be ignored
            var ignoreCertificateErrors = section.GetValue<bool?>("ignore_target_route_certificate_errors");
            // if no ignore certificate errors for this route, check if there are default ignore certificate errors for all routes
            ignoreCertificateErrors ??= _configuration.GetValue<bool?>("ignore_target_route_certificate_errors");
            #endregion

            using (var client = ignoreCertificateErrors == true
                ? httpClientFactory.CreateClient("ignoreCertificateErrors")
                : httpClientFactory.CreateClient()
                )
            {
                var targetRouteResponse = await client.SendAsync(targetRequestMsg, context.RequestAborted);

                #region check if we should cache this status code
                var memorySection = section.GetSection("cache:memory");
                if (memorySection.Exists())
                {
                    var excludeStatusCodesCsv = memorySection.GetValue<string?>("exclude_status_codes_from_cache") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(excludeStatusCodesCsv))
                    {
                        var excludedStatusCodes = excludeStatusCodesCsv
                            .Split(new char[] { ',', ' ', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(x => int.TryParse(x, out var code) ? code : -1)
                            .Where(x => x >= 0)
                            .ToHashSet();

                        if (excludedStatusCodes.Contains((int)targetRouteResponse.StatusCode))
                        {
                            // This status code should not be cached - stream directly
                            disableStreaming = false;
                        }
                    }
                }
                #endregion

                if (disableStreaming)
                {
                    // Buffer the entire response for caching
                    return await CachableHttpResponseContainer.Parse(targetRouteResponse);
                }
                else
                {
                    // Stream directly to client (no caching or status code excluded from cache)
                    context.Response.StatusCode = (int)targetRouteResponse.StatusCode;

                    #region setup the response headers back to the caller
                    // Copy response headers (general headers)
                    foreach (var header in targetRouteResponse.Headers
                        .Where(x => !_excludedResponseHeaders.Contains(x.Key))
                        )
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }

                    // Copy content headers (Content-Type, Content-Encoding, etc.)
                    foreach (var header in targetRouteResponse.Content.Headers
                        .Where(x => !_excludedResponseHeaders.Contains(x.Key))
                        )
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                    #endregion

                    // Stream the response body
                    await targetRouteResponse.Content.CopyToAsync(
                        context.Response.BodyWriter.AsStream(),
                        context.RequestAborted
                        );

                    context.Response.BodyWriter.Complete();
                    return null; // Indicates response already written
                }
            }
        }

        /// <summary>
        /// Writes a cached HTTP response to the client.
        /// </summary>
        /// <param name="context">The HTTP context.</param>
        /// <param name="cachedResponse">The cached response container.</param>
        private async Task WriteHttpResponseFromCache(HttpContext context, CachableHttpResponseContainer cachedResponse)
        {
            context.Response.StatusCode = cachedResponse.StatusCode;

            // Copy headers from cache
            foreach (var header in cachedResponse.Headers)
            {
                context.Response.Headers[header.Key] = header.Value;
            }

            // Copy content headers from cache
            foreach (var header in cachedResponse.ContentHeaders)
            {
                context.Response.Headers[header.Key] = header.Value;
            }

            // Write the cached content
            await context.Response.BodyWriter.WriteAsync(cachedResponse.Content, context.RequestAborted);
            context.Response.BodyWriter.Complete();
        }
    }
}
