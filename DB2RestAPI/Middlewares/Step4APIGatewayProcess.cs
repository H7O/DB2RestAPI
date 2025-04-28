using Azure;
using Azure.Core;
using Com.H.Data;
using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;

namespace DB2RestAPI.Middlewares
{
    /// <summary>
    /// route, section, service_type should already be available 
    /// in the context.Items when this middleware is called.
    /// `route` is a string representing the route of the request.
    /// `section` is an IConfigurationSection representing the configuration section of the route.
    /// `service_type` is a string representing the type of service. Current supported services are `api_gateway` and `db_query`
    /// 
    /// `parameters` and `payload` should not be read unless there is a `cache` tak in `section` (feature not yet implemented) 
    /// 
    /// `parameters` (once cache is implemented) is a List<Com.H.Data.Common.QueryParams> representing the parameters of the request.
    /// Which includes the query string parameters, the body parameters and headers.
    /// `payload` (once cache is implemented) is a JsonElement representing the body of the request.
    /// 
    /// If `service_type` is `api_gateway`, this middleware is designed to act as an API Gateway proxying the request 
    /// to an external API, retrieving the response and returning it to the caller.
    /// 
    /// If `service_type` is not `api_gateway` (e.g., `db_query`), this middleware is designed to pass the request to the next middleware 
    /// without any modifications.
    /// </summary>

    public class Step4APIGatewayProcess(
        RequestDelegate next,
        IConfiguration configuration,
        SettingsService settings,
        IHttpClientFactory httpClientFactory,
        ILogger<Step4APIGatewayProcess> logger
            )
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly IConfiguration _configuration = configuration;
        private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
        private readonly ILogger<Step4APIGatewayProcess> _logger = logger;
        private static int count = 0;
        private static readonly string[] excludedResponseHeaders = new string[] { "Transfer-Encoding", "Content-Length" };

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
                            // SMWSE6: standard middleware section error 6
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

            #region if no service type passed from the previous middlewares, return 500
            if (!context.Items.ContainsKey("serivce_type"))
            {

                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            // SMWSTE6: standard middleware service type error 6
                            message = "Improper service setup. (Contact your service provider support and provide them with error code `SMWSTE6`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
            }
            #endregion

            #region if service type is not `api_gateway`, call the next middleware
            if (context.Items["serivce_type"] as string != "api_gateway")
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
                            // SMWSE6: standard middleware section error 6
                            message = "Improper route settings (missing `url`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
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
                // url += (url.Contains("?") ? "&" : "?") + context.Request.QueryString.Value.Substring(1);
            }

            #endregion


            // route the current request (with headers and action to url)
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
                    _ = targetRequestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value);
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
                _ = targetRequestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            #endregion


            try
            {
                #region check if the target route certificate errors should be ignored
                var ignoreCertificateErrors = section.GetValue<bool?>("ignore_target_route_certificate_errors");
                // if no ignore certificate errors for this route, check if there are default ignore certificate errors for all routes
                ignoreCertificateErrors ??= _configuration.GetValue<bool?>("default_ignore_target_route_certificate_errors");
                #endregion

                using (var client = ignoreCertificateErrors == true
                    ? httpClientFactory.CreateClient("ignoreCertificateErrors")
                    : httpClientFactory.CreateClient()
                    )

                {

                    var targetRouteResponse = await client.SendAsync(targetRequestMsg); // , HttpCompletionOption.ResponseHeadersRead);

                    context.Response.StatusCode = (int)targetRouteResponse.StatusCode;



                    #region setup the response headers back to the caller
                    foreach (var header in targetRouteResponse.Headers
                        // Exclude Transfer-Encoding and Content-Length headers from being copied
                        // These headers must be managed by ASP.NET Core automatically:
                        // - Content-Length: Must reflect the actual response body size
                        // - Transfer-Encoding: Must align with how ASP.NET Core chunks the response
                        // Manually setting these headers would conflict with ASP.NET Core's 
                        // handling of the response stream and could cause corruption or errors
                        .Where(x => !excludedResponseHeaders.Contains(x.Key))
                        )
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }

                    #endregion


                    // copy the proxy call stream to the response stream
                    await targetRouteResponse.Content.CopyToAsync(
                        context.Response.BodyWriter.AsStream(),
                        // cancel the request if the client disconnects
                        // this is to prevent the server from sending the response body to the client
                        // after the client has disconnected
                        context.RequestAborted
                        );

                    // Complete the response stream.
                    // although this is not strictly necessary, 
                    // as the asp.net core is expected to close the stream when the response is disposed of,
                    // keeping this explicit call is a defensive programming approach that ensures proper
                    // completion regardless of how the framework's internals 
                    // might change in future versions.
                    context.Response.BodyWriter.Complete();
                }
            }
            catch (Exception ex)
            {
                await context.Response.DeferredWriteAsJsonAsync(_settings.GetExceptionResponse(context.Request, ex));
            }

        }
    }
}
