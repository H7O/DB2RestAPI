using DB2RestAPI.Cache;
using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;

namespace DB2RestAPI.Middlewares
{
    public class Step5JwtAuthorization(
                RequestDelegate next,
        IConfiguration configuration,
        ILogger<Step5JwtAuthorization> logger,
        CacheService cacheService)
    {
        private readonly RequestDelegate _next = next;
        private readonly IConfiguration _configuration = configuration;
        private readonly ILogger<Step5JwtAuthorization> _logger = logger;
        private readonly CacheService _cacheService = cacheService;
        private static readonly string _errorCode = "Step 5 - JWT Authorization";

        public async Task InvokeAsync(HttpContext context)
        {

            #region log the time and the middleware name
            this._logger.LogDebug("{time}: in Step5JwtAuthorization middleware",
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

            var route = context.Items.ContainsKey("route")
                ? context.Items["route"] as string
                : null;

            #region Check if authorization is required
            var routeAuthorizeSection = section.GetSection("authorize");

            // If no authorization configured, pass through
            if (!routeAuthorizeSection.Exists())
            {
                await _next(context);
                return;
            }

            // Check if authorization is explicitly disabled
            var enabled = routeAuthorizeSection.GetValue<bool?>("enabled") ?? true;
            if (!enabled)
            {
                _logger.LogDebug($"Authorization explicitly disabled for route `{route}`");
                await _next(context);
                return;
            }
            #endregion


            #region Get provider configuration
            var providerName = routeAuthorizeSection.GetValue<string>("provider");

            IConfigurationSection? providerSection = null;
            if (!string.IsNullOrWhiteSpace(providerName))
            {
                // Look for provider in oidc_providers configuration
                providerSection = _configuration.GetSection($"authorize:providers:{providerName}");

                if (!providerSection.Exists())
                {
                    _logger.LogError("Provider '{providerName}' not found in configuration for route `{route}`", providerName, route);
                    await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            new
                            {
                                success = false,
                                message = $"Authorization provider configuration error. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            }
                        )
                        {
                            StatusCode = 500
                        }
                    );
                    return;
                }
            }
            #endregion


            #region Get JWT configuration (route > provider > global)
            var authority = routeAuthorizeSection.GetValue<string>("authority")
                            ?? providerSection?.GetValue<string>("authority")
                            ?? _configuration.GetValue<string>("authorize:authority");

            var audience = routeAuthorizeSection.GetValue<string>("audience")
                           ?? providerSection?.GetValue<string>("audience")
                           ?? _configuration.GetValue<string>("authorize:audience");

            var issuer = routeAuthorizeSection.GetValue<string>("issuer")
                         ?? providerSection?.GetValue<string>("issuer")
                         ?? _configuration.GetValue<string>("authorize:issuer")
                         ?? authority;

            var validateIssuer = routeAuthorizeSection.GetValue<bool?>("validate_issuer")
                                 ?? providerSection?.GetValue<bool?>("validate_issuer")
                                 ?? _configuration.GetValue<bool?>("authorize:validate_issuer")
                                 ?? true;

            var validateAudience = routeAuthorizeSection.GetValue<bool?>("validate_audience")
                                   ?? providerSection?.GetValue<bool?>("validate_audience")
                                   ?? _configuration.GetValue<bool?>("authorize:validate_audience")
                                   ?? true;

            var validateLifetime = routeAuthorizeSection.GetValue<bool?>("validate_lifetime")
                                   ?? providerSection?.GetValue<bool?>("validate_lifetime")
                                   ?? _configuration.GetValue<bool?>("authorize:validate_lifetime")
                                   ?? true;

            var clockSkewSeconds = routeAuthorizeSection.GetValue<int?>("clock_skew_seconds")
                                   ?? providerSection?.GetValue<int?>("clock_skew_seconds")
                                   ?? _configuration.GetValue<int?>("authorize:clock_skew_seconds")
                                   ?? 300;

            // Get UserInfo fallback configuration
            var userInfoFallbackClaims = routeAuthorizeSection.GetValue<string>("userinfo_fallback_claims")
                                         ?? providerSection?.GetValue<string>("userinfo_fallback_claims")
                                         ?? _configuration.GetValue<string>("authorize:userinfo_fallback_claims")
                                         ?? "email,name,given_name,family_name";

            var userInfoCacheDuration = routeAuthorizeSection.GetValue<int?>("userinfo_cache_duration_seconds")
                                        ?? providerSection?.GetValue<int?>("userinfo_cache_duration_seconds")
                                        ?? _configuration.GetValue<int?>("authorize:userinfo_cache_duration_seconds");
            // Note: If null, cache will default to token expiration time

            if (string.IsNullOrWhiteSpace(authority))
            {
                _logger.LogError("JWT authority not configured for route `{route}", route);
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Authorization configuration error. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }
            #endregion

            #region Extract Bearer token
            if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader)
                || string.IsNullOrWhiteSpace(authHeader.ToString()))
            {
                _logger.LogDebug("Missing Authorization header for route `{route}`", route);
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Authorization header is required"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }

            var authHeaderValue = authHeader.ToString();
            if (!authHeaderValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Authorization header must use Bearer scheme");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Invalid authorization header format. Use: Bearer <token>"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }

            var accessToken = authHeaderValue.Substring("Bearer ".Length).Trim();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogDebug("Empty Bearer token in route `{route}`", route);
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Bearer token is required"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }
            #endregion



        }


        private async Task<OpenIdConnectConfiguration> GetDiscoveryDocumentAsync(
            string authority,
            CancellationToken cancellationToken)
        {
            var normalizedAuthority = authority.TrimEnd('/');
            var cacheKey = $"oidc_discovery:{normalizedAuthority}";
            
            // Cache discovery documents for 24 hours (common practice for OIDC metadata)
            var cacheDuration = TimeSpan.FromHours(24);

            return await _cacheService.GetAsync(
                cacheKey,
                cacheDuration,
                async (ct) =>
                {
                    var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        $"{normalizedAuthority}/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever());

                    return await configurationManager.GetConfigurationAsync(ct);
                },
                cancellationToken);
        }





    }
}