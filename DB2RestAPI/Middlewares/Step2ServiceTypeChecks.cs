using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;


namespace DB2RestAPI.Middlewares;

/// <summary>
/// This middleware determines the service type of the incoming request by analyzing the route.
/// It checks whether the request should be handled as an API gateway route (proxying to external APIs)
/// or as a database query route (executing configured SQL queries).
/// 
/// The middleware validates that:
/// - The route is properly formatted and exists
/// - The Content-Type header is set to application/json (or the route starts with json/)
/// - The configuration sections for either API gateway or database queries are properly defined
/// 
/// Upon successful validation, it sets the following items in context.Items:
/// - `route`: A string representing the route of the request
/// - `section`: An IConfigurationSection representing the configuration section of the route
/// - `service_type`: A string representing the type of service (`api_gateway` or `db_query`)
/// - `remaining_path` (for api_gateway only): The remaining path after the route match
/// - `route_parameters` (for db_query only): Dictionary of route parameters if any exist
/// - `content_type`: The content type of the request
/// </summary>
public class Step2ServiceTypeChecks(
    RequestDelegate next,
    IConfiguration configuration,
    ILogger<Step2ServiceTypeChecks> logger,
    RouteConfigResolver routeConfigResolver,
    QueryRouteResolver queryRouteResolver
        )
{

    private readonly RequestDelegate _next = next;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<Step2ServiceTypeChecks> _logger = logger;
    private readonly RouteConfigResolver _routeConfigResolver = routeConfigResolver;
    private readonly QueryRouteResolver _queryRouteResolver = queryRouteResolver;
    private readonly HashSet<string> _acceptableContentTypes = new HashSet<string>
    {
        "application/json",
        "multipart/form-data",
        "application/x-www-form-urlencoded"
    };
    
    // private static int count = 0;
    private static readonly string _errorCode = "Step 2 - Service Type Check Error";

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
                        message = $"Improper route settings. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    })
                {
                    StatusCode = 500
                });
            return;
        }
        #endregion

        // var verb = context.Request.Method;


        #region extract route data from the request
        // get route data from the request and extract the api endpoint name
        // from multiple segments separated by `/`
        // then replace `$2F` with `/` in the endpoint name
        var route = routeValue?
            .ToString()?
            .Replace("$2F", "/");
        #endregion

        #region check content-type
        // check if user set Content-Type header of the request to `application/json` or `multipart/form-data`,
        // or set the route starts with `json/`. If NOT, return a message stating that
        // caller should set the Content-Type header to either `application/json` or `multipart/form-data`
        // with error code 400

        var contentType = context.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();

        // string? mediaType = contentType?.Split(';')[0].Trim().ToLowerInvariant();

        //if (
        //    !(contentType!=null && _acceptableContentTypes.Contains(contentType.ToLower())
        //    || route?.StartsWith("json/") == true
        //    )
        //)
        //{

        //    await context.Response.DeferredWriteAsJsonAsync(
        //        new ObjectResult(
        //            new
        //            {
        //                success = false,
        //                message = "`Content-Type` header must be set to `application/json` or `multipart/form-data, or endpoint should start with `json/`"
        //            })
        //        {
        //            StatusCode = 400
        //        });
        //    return;
        //}

        // if the route starts with `json/`, remove the `json/` prefix
        if (route?.StartsWith("json/") == true)
        {
            route = DefaultRegex.DefaultRemoveJsonPrefixFromRouteCompiledRegex.Replace(route, string.Empty);
            contentType = "application/json";
        }

        if (string.IsNullOrWhiteSpace(contentType))
            contentType = "application/json";

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
            context.Items["content_type"] = contentType;
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

        var serviceQuerySection = this._queryRouteResolver.ResolveRoute(route, context.Request.Method);

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

        var routeParameters = this._queryRouteResolver.GetRouteParametersIfAny(serviceQuerySection, route);

        context.Items["route"] = route;
        context.Items["section"] = serviceQuerySection;
        context.Items["service_type"] = "db_query";
        context.Items["content_type"] = contentType;

        if (routeParameters != null && routeParameters.Count > 0)
            context.Items["route_parameters"] = routeParameters;

        // Call the next middleware in the pipeline
        await _next(context);

        #endregion

    }
}
