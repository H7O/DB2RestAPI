using Azure;
using DB2RestAPI.Cache;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using DB2RestAPI.Settings.Extensinos;
using DB2RestAPI.Settings;

namespace DB2RestAPI.Functions
{
    public class RoutingGateway
    {
        #region API gateway functionality
        private async Task<IActionResult> GetRoutedResponse(
                    IConfigurationSection routeSection,
                    IConfiguration configuration,
                    SettingsService settings,
                    JsonElement payload,
                    HttpRequest request,
                    IHttpClientFactory httpClientFactory,
                    HttpResponse response
                    )
        {

            #region local API keys check 
            var failedAPIKeysCheck = routeSection
                .GetFailedAPIKeysCheckResponseIfAny(configuration, request);
            if (failedAPIKeysCheck != null)
                return failedAPIKeysCheck;
            #endregion
            var url = routeSection.GetValue<string>("url");

            if (string.IsNullOrWhiteSpace(url))
                return new ObjectResult(new { success = false, message = $"Improper route settings" })
                {
                    StatusCode = 400
                };

            var mandatoryParameters = settings.GetMandatoryParameters(routeSection);
            if (mandatoryParameters != null
                && mandatoryParameters.Length > 0
                )
            {
                var qParams = settings.GetParams(routeSection, request, payload);
                var failedMandatoryParamsCheck = settings
                    .GetFailedMandatoryParamsCheckIfAny(routeSection, qParams);
                if (failedMandatoryParamsCheck != null)
                    return failedMandatoryParamsCheck;
            }
            // check if `this.Request` has query string
            // if queryString has values, append it to the url, and if the url already has a query string, append it with `&`
            if (!string.IsNullOrWhiteSpace(request.QueryString.Value))
            {
                url += string.Concat(url.Contains('?') ? "&" : "?", request.QueryString.Value.AsSpan(1));
                // url += (url.Contains("?") ? "&" : "?") + request.QueryString.Value.Substring(1);
            }

            // route the current targetRequestMsg (with headers and action to url)
            var requestMsg = new HttpRequestMessage(new HttpMethod(request.Method), url);
            requestMsg.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

            // var passApiKey = routeSection.GetValue<bool?>("pass_api_key") ?? false;

            // get headers from the current targetRequestMsg and add them to the new targetRequestMsg

            // see if there are headers that should not be passed to the server for this particular route
            var headersToExclude = routeSection.GetValue<string>("headers_to_exclude_from_routing")?
                .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);


            // .GetSection("headers_to_exclude_from_routing")?.GetChildren().Select(x => x.Value).ToArray();
            if (headersToExclude == null || headersToExclude.Length < 1)
                // check if there are default headers to exclude for all routes
                headersToExclude = configuration.GetValue<string>("default_headers_to_exclude_from_routing")?
                    .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // .GetSection("default_headers_to_exclude_from_routing")?.GetChildren().Select(x => x.Value).ToArray();

            // see if there are headers to override for this particular route defined in the config file under:
            //       <headers>
            //          <header>
            //              <name>x-api-cacheKey</name>
            //              <value>local api cacheKey 1</value>
            //          </header>
            //      </headers>
            var headersToOverride = routeSection.GetSection("headers")?.GetChildren()?
                // remove null `name` headers
                .Where(x => !string.IsNullOrWhiteSpace(x.GetValue<string>("name")))
                .Select(x => new KeyValuePair<string, string>(x.GetValue<string>("name")!,
                x.GetValue<string>("value") ?? string.Empty))
                .ToDictionary(x => x.Key, x => x.Value);

            if (headersToOverride?.Count > 0 == true)
            {
                foreach (var header in headersToOverride)
                {
                    _ = requestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            foreach (var header in request.Headers)
            {

                if (
                    // exclude headers that should not be passed to the server (make sure to accomodate for case sensitivity)
                    headersToExclude?.Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
                    || headersToOverride?.Select(x => x.Key).Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
                    )
                    continue;
                _ = requestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            try
            {
                var ignoreCertificateErrors = routeSection.GetValue<bool?>("ignore_target_route_certificate_errors");

                ignoreCertificateErrors ??= configuration.GetValue<bool?>("default_ignore_target_route_certificate_errors");
                using (var client = ignoreCertificateErrors == true
                    ? httpClientFactory.CreateClient("ignoreCertificateErrors")
                    : httpClientFactory.CreateClient()
                    )

                {

                    var routeResponse = await client.SendAsync(requestMsg); // , HttpCompletionOption.ResponseHeadersRead);


                    // proxy the responseToCaller back to the caller as is (i.e., without processing)

                    // setup the responseToCaller content headers
                    foreach (var header in routeResponse.Content.Headers)
                    {
                        response.Headers[header.Key] = header.Value.ToArray();
                    }

                    // setup the responseToCaller headers
                    foreach (var header in routeResponse.Headers
                        // exclude `Transfer-Encoding` and `Content-Length` headers
                        // as they are set by the server automatically
                        // and should not be set manually by the proxy
                        // reason for that is that the server will set the `Content-Length` header
                        // based on the actual content length, and if the proxy sets it manually
                        // it may cause issues with the responseToCaller stream.
                        // And the reason why we exclude `Transfer-Encoding` header is that
                        // the server will set it based on the responseToCaller content type
                        // and the proxy should not set it manually
                        // as it may cause issues with the responseToCaller stream.
                        .Where(x => !new string[] { "Transfer-Encoding", "Content-Length" }.Contains(x.Key))
                        )
                    {
                        response.Headers[header.Key] = header.Value.ToArray();
                    }

                    // copy the proxy call stream to the responseToCaller stream
                    await routeResponse.Content.CopyToAsync(response.BodyWriter.AsStream());

                    // complete the responseToCaller stream
                    response.BodyWriter.Complete();

                    // return an empty result (since the responseToCaller is already sent)
                    return new EmptyResult();
                }


            }
            catch (Exception ex)
            {
                return settings.GetExceptionResponse(request, ex);
            }
            // use ObjectResult to return a responseToCaller with status code 400 (similar to BadRequest)

            // return BadRequest(new { success = false, message = $"Improper route settings" });

        }


        //public async Task<IActionResult> GetCachableRoutedResponse(
        //            IConfigurationSection routeSection,
        //            IConfiguration configuration,
        //            SettingsService settings,
        //            JsonElement payload,
        //            HttpRequest callerRequest,
        //            IHttpClientFactory httpClientFactory,
        //            HttpResponse responseToCaller
        //            )
        //{

        //    #region local API keys check 
        //    var failedAPIKeysCheck = settings.GetFailedAPIKeysCheckResponseIfAny(routeSection, callerRequest);
        //    if (failedAPIKeysCheck != null)
        //        return failedAPIKeysCheck;
        //    #endregion

        //    #region url check
        //    var url = routeSection.GetValue<string>("url");

        //    if (string.IsNullOrWhiteSpace(url))
        //        return new ObjectResult(new { success = false, message = $"Improper route settings" })
        //        {
        //            StatusCode = 400
        //        };
        //    #endregion


        //    #region url request construction
        //    // check if `this.Request` has query string
        //    // if queryString has values, append it to the url, and if the url already has a query string, append it with `&`
        //    if (!string.IsNullOrWhiteSpace(callerRequest.QueryString.Value))
        //    {
        //        url += (url.Contains("?") ? "&" : "?") + callerRequest.QueryString.Value.Substring(1);
        //    }

        //    #endregion

        //    // route the current targetRequestMsg (with headers and action to url)


        //    var targetRequestMsg = new HttpRequestMessage(new HttpMethod(callerRequest.Method), url);
        //    targetRequestMsg.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

        //    // var passApiKey = routeSection.GetValue<bool?>("pass_api_key") ?? false;

        //    // get headers from the current targetRequestMsg and add them to the new targetRequestMsg

        //    // see if there are headers that should not be passed to the server for this particular route
        //    var headersToExclude = routeSection.GetValue<string>("headers_to_exclude_from_routing")?
        //        .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);


        //    if (headersToExclude == null || headersToExclude.Length < 1)
        //        // check if there are default headers to exclude for all routes
        //        headersToExclude = configuration.GetValue<string>("default_headers_to_exclude_from_routing")?
        //            .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        //    // see if there are headers to override for this particular route defined in the config file under:
        //    //       <headers>
        //    //          <header>
        //    //              <name>x-api-cacheKey</name>
        //    //              <value>local api cacheKey 1</value>
        //    //          </header>
        //    //      </headers>
        //    var headersToOverride = routeSection.GetSection("headers_to_override")?.GetChildren()?
        //        // remove null `name` headers
        //        .Where(x => !string.IsNullOrWhiteSpace(x.GetValue<string>("name")))
        //        .Select(x => new KeyValuePair<string, string>(x.GetValue<string>("name")!,
        //        x.GetValue<string>("value") ?? string.Empty))
        //        .ToDictionary(x => x.Key, x => x.Value);

        //    if (headersToOverride?.Count > 0 == true)
        //    {
        //        foreach (var header in headersToOverride)
        //        {
        //            _ = targetRequestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value);
        //        }
        //    }

        //    foreach (var header in callerRequest.Headers)
        //    {

        //        if (
        //            // exclude headers that should not be passed to the server (make sure to accomodate for case sensitivity)
        //            headersToExclude?.Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
        //            || headersToOverride?.Select(x => x.Key).Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
        //            )
        //            continue;
        //        _ = targetRequestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        //    }

        //    try
        //    {

        //        CachableHttpResponseContainer responseContainer = await GetHttpResponseContainer(routeSection, targetRequestMsg);

        //        #region set the response http context headers
        //        foreach (var header in responseContainer.ContentHeaders)
        //        {
        //            responseToCaller.HttpContext.Response.Headers[header.Key] = header.Value.ToArray();
        //        }
        //        #endregion

        //        #region set the response headers
        //        foreach (var header in responseContainer.Headers)
        //        {
        //            callerRequest.Headers[header.Key] = header.Value.ToArray();
        //        }

        //        responseToCaller.BodyWriter.Complete();

        //    }
        //    catch (Exception ex)
        //    {
        //        return settings.GetExceptionResponse(callerRequest, ex);
        //    }

        //}


        private async Task<CachableHttpResponseContainer> GetHttpResponseContainer(
            IConfigurationSection routeSection, 
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            HttpRequestMessage callerResponseMsg,
            HttpResponse responseToCaller
            )
        {
            CachableHttpResponseContainer cachedResponse;


            var ignoreCertificateErrors = routeSection.GetValue<bool?>("ignore_certificate_errors");

            ignoreCertificateErrors ??= configuration.GetValue<bool?>("ignore_certificate_errors_when_routing");
            using (var client = ignoreCertificateErrors == true
                ? httpClientFactory.CreateClient("ignoreCertificateErrors")
                : httpClientFactory.CreateClient()
                )

            {

                var response = await client.SendAsync(callerResponseMsg); // , HttpCompletionOption.ResponseHeadersRead);

                cachedResponse = await CachableHttpResponseContainer.Parse(response);


                // proxy the responseToCaller back to the caller as is (i.e., without processing)

                // setup the responseToCaller content headers

                // setup the responseToCaller headers
                foreach (var header in response.Headers
                    // exclude `Transfer-Encoding` and `Content-Length` headers
                    // as they are set by the server automatically
                    // and should not be set manually by the proxy
                    // reason for that is that the server will set the `Content-Length` header
                    // based on the actual content length, and if the proxy sets it manually
                    // it may cause issues with the responseToCaller stream.
                    // And the reason why we exclude `Transfer-Encoding` header is that
                    // the server will set it based on the responseToCaller content type
                    // and the proxy should not set it manually
                    // as it may cause issues with the responseToCaller stream.
                    .Where(x => !new string[] { "Transfer-Encoding", "Content-Length" }.Contains(x.Key))
                    )
                {
                    responseToCaller.Headers[header.Key] = header.Value.ToArray();
                }

                var contentByteArray = await response.Content.ReadAsByteArrayAsync();

                // copy the proxy call stream to the responseToCaller stream
                await response.Content.CopyToAsync(responseToCaller.BodyWriter.AsStream());

                // complete the responseToCaller stream

            }

            return cachedResponse;
        }


        #endregion


    }
}
