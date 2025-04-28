using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;


namespace DB2RestAPI.Middlewares;
public class Step2ServiceTypeChecks(
    RequestDelegate next,
    IConfiguration configuration,
    ILogger<Step2ServiceTypeChecks> logger,
    RouteConfigResolver routeConfigResolver
        )
{

    private readonly RequestDelegate _next = next;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<Step2ServiceTypeChecks> _logger = logger;
    private readonly RouteConfigResolver _routeConfigResolver = routeConfigResolver;
    private static int count = 0;

    public async Task InvokeAsync(HttpContext context)
    {

        #region log the time and the middleware name
        this._logger.LogDebug("{time}: in Step2ServiceTypeChecks middleware",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
        #endregion

        // Retrieve the route data
        var routeData = context.GetRouteData();

        #region if route is null, return 404
        if (routeData == null
            ||
            !routeData.Values.TryGetValue("route", out var routeValue)
            )
        {

            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(
                    new
                    {
                        success = false,
                        message = "Improper route settings"
                    })
                {
                    StatusCode = 500
                });
            return;
        }
        #endregion


        #region extract route data from the request
        // get route data from the request and extract the api endpoint name
        // from multiple segments separated by `/`
        // then replace `$2F` with `/` in the endpoint name
        var route = routeValue?
            .ToString()?
            .Replace("$2F", "/");
        #endregion

        #region check content-type
        // check if user either set Content-Type header of the request to application/json
        // or set the route starts with `json/`, if not, return a message stating that
        // caller should set the Content-Type header to application/json 
        // with error code 400

        if (!context.Request.ContentType?.Contains("application/json") == true
            &&
            !route?.StartsWith("json/") == true
        )
        {

            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(
                    new
                    {
                        success = false,
                        message = "`Content-Type` header must be set to `application/json`"
                    })
                {
                    StatusCode = 400
                });
            return;
        }

        // if the route starts with `json/`, remove the `json/` prefix
        if (route?.StartsWith("json/") == true)
            route = DefaultRegex.DefaultRemoveJsonPrefixFromRouteCompiledRegex.Replace(route, string.Empty);

        if (string.IsNullOrWhiteSpace(route))
        {

            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(
                    new
                    {
                        success = false,
                        message = "Kindly specify an API endpoint"
                    })
                {
                    StatusCode = 404
                });

            return;
        }

        #endregion
        #region check if configuration is null
        if (this._configuration == null)
        {

            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(
                    new
                    {
                        success = false,
                        message = "Configuration is not set"
                    })
                {
                    StatusCode = 500
                });

            return;
        }
        #endregion



        #region checking if the request is an API gateway routing request

        var routeConfig = this._routeConfigResolver.ResolveRoute(route);

        if (routeConfig != null)
        {
            context.Items["route"] = route;
            context.Items["section"] = routeConfig;
            context.Items["service_type"] = "api_gateway";
            context.Items["remaining_path"] = this._routeConfigResolver.GetRemainingPath(route, routeConfig);
            // Call the next middleware in the pipeline
            await _next(context);
            return;
        }


        #endregion

        #region check if the route is to get data from DB
        var queries = this._configuration.GetSection("queries");

        if (queries == null || !queries.Exists())
        {

            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(
                    new
                    {
                        success = false,
                        message = "No API Endpoints defined"
                    })
                {
                    StatusCode = 404
                });


            return;
        }

        var serviceQuerySection = queries.GetSection(route);

        if (serviceQuerySection == null || !serviceQuerySection.Exists())
        {

            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(
                    new
                    {
                        success = false,
                        message = $"API Endpoint `{route}` not found"
                    })
                {
                    StatusCode = 404
                });
            return;
        }


        context.Items["route"] = route;
        context.Items["section"] = serviceQuerySection;
        context.Items["service_type"] = "db_query";
        // Call the next middleware in the pipeline
        await _next(context);

        #endregion

    }
}
