using DB2RestAPI.Cache;
using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

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


            #region Validate JWT access token
            ClaimsPrincipal principal;
            SecurityToken validatedToken;
            OpenIdConnectConfiguration? discoveryDocument;
            try
            {
                // Get or fetch discovery document (with caching)
                discoveryDocument = await GetDiscoveryDocumentAsync(authority, context.RequestAborted);

                // DEBUG: Log discovery document details
                _logger.LogDebug("Discovery document loaded from: {authority}", authority);
                _logger.LogDebug("Issuer from discovery: {issuer}", discoveryDocument.Issuer);
                _logger.LogDebug("JWKS URI: {jwksUri}", discoveryDocument.JwksUri);
                _logger.LogDebug("Number of signing keys: {count}", discoveryDocument.SigningKeys?.Count ?? 0);

                // Also log JsonWebKeySet keys
                var jwksKeyCount = discoveryDocument.JsonWebKeySet?.Keys?.Count ?? 0;
                _logger.LogDebug("Number of keys in JsonWebKeySet: {count}", jwksKeyCount);

                // Use keys from JsonWebKeySet if SigningKeys is empty but JsonWebKeySet has keys
                ICollection<SecurityKey>? keysToUse = discoveryDocument.SigningKeys;
                if ((!discoveryDocument.SigningKeys?.Any() ?? true) && jwksKeyCount > 0)
                {
                    _logger.LogDebug("Using keys from JsonWebKeySet instead of SigningKeys");
                    keysToUse = discoveryDocument.JsonWebKeySet?.GetSigningKeys();
                }

                if (keysToUse == null || !keysToUse.Any())
                {
                    _logger.LogError("No signing keys found in discovery document. JWKS URI: {jwksUri}", discoveryDocument.JwksUri);
                    throw new InvalidOperationException("No signing keys available from OIDC provider");
                }

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = keysToUse,
                    ValidateIssuer = validateIssuer,
                    ValidIssuer = issuer,
                    ValidateAudience = validateAudience,
                    ValidAudience = audience,
                    ValidateLifetime = validateLifetime,
                    ClockSkew = TimeSpan.FromSeconds(clockSkewSeconds)
                };

                var tokenHandler = new JwtSecurityTokenHandler();
                principal = tokenHandler.ValidateToken(accessToken, validationParameters, out validatedToken);

                _logger.LogDebug("Access token validation successful");
            }
            catch (SecurityTokenExpiredException)
            {
                _logger.LogDebug("Access token expired");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Token has expired"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }
            catch (SecurityTokenInvalidSignatureException)
            {
                _logger.LogWarning("Access token has invalid signature");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Invalid token signature"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }
            catch (SecurityTokenException ex)
            {
                _logger.LogWarning(ex, "Access token validation failed");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Invalid token"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during token validation");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Authorization error. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }
            #endregion


            #region Extract basic claims from access token
            //Dictionary<string, object> claimsDict = validatedToken is JwtSecurityToken jwtToken
            //    ? jwtToken.Payload.ToDictionary()
            //    : principal.Claims.ToDictionary(c => c.Type, c => (object)c.Value);

            Dictionary<string, object> claimsDict = new Dictionary<string, object>();

            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                         ?? principal.FindFirst("sub")?.Value
                         ?? principal.FindFirst("oid")?.Value;
            //if (!string.IsNullOrWhiteSpace(userId))
            //    claimsDict["user_id"] = userId;

            var userEmail = principal.FindFirst(ClaimTypes.Email)?.Value
                            ?? principal.FindFirst("email")?.Value
                            ?? principal.FindFirst("emails")?.Value;
            //if (!string.IsNullOrWhiteSpace(userEmail))
            //    claimsDict["email"] = userEmail;

            var userName = principal.FindFirst(ClaimTypes.Name)?.Value
                           ?? principal.FindFirst("name")?.Value;
            //if (!string.IsNullOrWhiteSpace(userName))
            //    claimsDict["name"] = userName;

            var userRoles = principal.FindAll(ClaimTypes.Role)
                .Concat(principal.FindAll("roles"))
                .Select(c => c.Value)
                .Distinct()
                .ToList();
            //if (userRoles?.Any() == true)
            //    claimsDict["roles"] = string.Join("|", userRoles);
            #endregion



            #region UserInfo endpoint fallback for missing claims
            var fallbackClaimsList = userInfoFallbackClaims
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var missingClaims = new List<string>();

            if (fallbackClaimsList.Contains("email") && string.IsNullOrWhiteSpace(userEmail))
                missingClaims.Add("email");

            if (fallbackClaimsList.Contains("name") && string.IsNullOrWhiteSpace(userName))
                missingClaims.Add("name");

            if (fallbackClaimsList.Contains("given_name") && !principal.HasClaim(c => c.Type == "given_name"))
                missingClaims.Add("given_name");

            if (fallbackClaimsList.Contains("family_name") && !principal.HasClaim(c => c.Type == "family_name"))
                missingClaims.Add("family_name");

            if (missingClaims.Any())
            {
                _logger.LogDebug("Missing claims in access token: {claims}. Calling UserInfo endpoint...",
                    string.Join(", ", missingClaims));

                // var discoveryDoc = await GetDiscoveryDocumentAsync(authority, context.RequestAborted);
                var userInfoClaims = await GetUserInfoAsync(
                    accessToken,
                    discoveryDocument,
                    userInfoCacheDuration,
                    validatedToken.ValidTo,  // Pass token expiration
                    context.RequestAborted);

                if (userInfoClaims != null && userInfoClaims.Any())
                {
                    // Add UserInfo claims to principal
                    var identity = principal.Identity as ClaimsIdentity;
                    foreach (var claim in userInfoClaims)
                    {
                        // Only add if not already present
                        if (!principal.HasClaim(c => c.Type == claim.Key))
                        {
                            identity?.AddClaim(new Claim(claim.Key, claim.Value?.ToString() ?? string.Empty));
                            if (!string.IsNullOrWhiteSpace(claim.Value?.ToString()))
                                claimsDict[claim.Key] = claim.Value;
                        }
                    }

                    // Re-extract claims after UserInfo merge
                    userEmail ??= userInfoClaims.TryGetValue("email", out var emailObj)
                        ? emailObj?.ToString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(userEmail))
                        claimsDict["email"] = userEmail;

                    userName ??= userInfoClaims.TryGetValue("name", out var nameObj)
                        ? nameObj?.ToString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(userName))
                        claimsDict["name"] = userName;

                    _logger.LogDebug("UserInfo claims added successfully");
                }
                else
                {
                    _logger.LogWarning("Failed to retrieve UserInfo claims");
                }
            }
            #endregion


            #region Store claims in context for downstream use
            // context.Items["user_claims"] = principal;
            context.User = principal;

            if (!string.IsNullOrWhiteSpace(userId))
                claimsDict["user_id"] = userId;

            if (!string.IsNullOrWhiteSpace(userEmail))
                claimsDict["email"] = userEmail;

            if (!string.IsNullOrWhiteSpace(userName))
                claimsDict["name"] = userName;

            if (userRoles?.Any() == true)
                claimsDict["roles"] = string.Join("|", userRoles);



            // Store all OIDC claims for SQL access
            foreach (var claim in principal.Claims)
            {
                if (!claimsDict.ContainsKey(claim.Type))
                    claimsDict[claim.Type] = claim.Value;
            }

            context.Items["user_claims"] = claimsDict;

            _logger.LogDebug("User context set successfully. UserId: {userId}, Email: {email}",
                userId ?? "unknown", userEmail ?? "unknown");
            #endregion


            #region Check required scopes
            var requiredScopes = routeAuthorizeSection.GetValue<string>("required_scopes")
                                 ?? providerSection?.GetValue<string>("required_scopes");

            if (!string.IsNullOrWhiteSpace(requiredScopes))
            {
                var scopes = principal.FindAll("scp")
                    .Concat(principal.FindAll("scope"))
                    .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Distinct()
                    .ToHashSet();

                var required = requiredScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (!required.All(r => scopes.Contains(r)))
                {
                    _logger.LogWarning("Missing required scopes. Required: {required}, Found: {scopes}",
                        string.Join(", ", required), string.Join(", ", scopes));

                    await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            new
                            {
                                success = false,
                                message = "Insufficient permissions"
                            }
                        )
                        {
                            StatusCode = 403
                        }
                    );
                    return;
                }
            }
            #endregion

            #region Check required roles
            var requiredRoles = routeAuthorizeSection.GetValue<string>("required_roles")
                                ?? providerSection?.GetValue<string>("required_roles");

            if (!string.IsNullOrWhiteSpace(requiredRoles))
            {
                var required = requiredRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (!required.All(r => userRoles.Contains(r, StringComparer.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Missing required roles. Required: {required}, Found: {roles}",
                        string.Join(", ", required), string.Join(", ", userRoles));

                    await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            new
                            {
                                success = false,
                                message = "Insufficient permissions"
                            }
                        )
                        {
                            StatusCode = 403
                        }
                    );
                    return;
                }
            }
            #endregion

            // Validation successful, proceed to next middleware
            await _next(context);


        }

        /// <summary>
        /// Gets the OIDC discovery document with caching.
        /// Uses CachedOpenIdConnectConfiguration to properly serialize/deserialize signing keys through HybridCache.
        /// </summary>
        private async Task<OpenIdConnectConfiguration> GetDiscoveryDocumentAsync(
            string authority,
            CancellationToken cancellationToken)
        {
            var normalizedAuthority = authority.TrimEnd('/');
            var cacheKey = $"oidc_discovery:{normalizedAuthority}";

            // Cache discovery documents for 24 hours (common practice for OIDC metadata)
            var cacheDuration = TimeSpan.FromHours(24);

            var cachedConfig = await _cacheService.GetAsync(
                cacheKey,
                cacheDuration,
                async (ct) =>
                {
                    var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        $"{normalizedAuthority}/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever());

                    var config = await configurationManager.GetConfigurationAsync(ct);

                    // Fetch JWKS JSON separately to ensure proper serialization through cache
                    // (OpenIdConnectConfiguration.SigningKeys doesn't serialize properly with HybridCache)
                    var jwksJson = await new HttpDocumentRetriever().GetDocumentAsync(config.JwksUri!, ct);
                    _logger.LogDebug("Fetched JWKS from {uri}", config.JwksUri);

                    // Create cacheable wrapper that stores JWKS as JSON string
                    return CachedOpenIdConnectConfiguration.FromDiscoveryDocument(config, jwksJson);
                },
                cancellationToken);

            // Convert cached wrapper back to OpenIdConnectConfiguration with signing keys properly populated
            return cachedConfig.ToDiscoveryDocument();
        }

        /// <summary>
        /// Calls the UserInfo endpoint with the access token to retrieve additional user claims.
        /// Results are cached using SHA-256 hash of the access token.
        /// 
        /// Smart Caching Strategy:
        /// - If userInfoCacheDuration is configured, it acts as the MAXIMUM cache duration
        /// - Cache NEVER outlives the access token's expiration
        /// - If userInfoCacheDuration is null/0, defaults to token's expiration time
        /// - Example: Token expires in 3600s, max cache is 300s → cache for 300s
        /// - Example: Token expires in 120s, max cache is 300s → cache for 120s (token expiry wins)
        /// - Example: Token expires in 3600s, no max configured → cache for 3600s
        /// </summary>
        private async Task<Dictionary<string, object>?> GetUserInfoAsync(
            string accessToken,
            Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration discoveryDocument,
            int? userInfoCacheDuration,
            DateTime tokenExpiration,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(discoveryDocument.UserInfoEndpoint))
            {
                _logger.LogWarning("UserInfo endpoint not defined in discovery document");
                return null;
            }
            // Compute SHA-256 hash of the access token for cache key
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var tokenHashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(accessToken));
            var tokenHashString = Convert.ToBase64String(tokenHashBytes);
            var cacheKey = $"userinfo_claims:{tokenHashString}";
            // Determine cache duration
            TimeSpan effectiveCacheDuration;
            if (userInfoCacheDuration.HasValue && userInfoCacheDuration.Value > 0)
            {
                var maxCache = TimeSpan.FromSeconds(userInfoCacheDuration.Value);
                var timeToTokenExpiry = tokenExpiration - DateTime.UtcNow;
                effectiveCacheDuration = timeToTokenExpiry < maxCache ? timeToTokenExpiry : maxCache;
            }
            else
            {
                effectiveCacheDuration = tokenExpiration - DateTime.UtcNow;
            }
            if (effectiveCacheDuration <= TimeSpan.Zero)
            {
                _logger.LogDebug("Token already expired, skipping UserInfo call");
                return null;
            }
            return await _cacheService.GetAsync(
                cacheKey,
                effectiveCacheDuration,
                async (ct) =>
                {
                    using var httpClient = new HttpClient();
                    var request = new HttpRequestMessage(HttpMethod.Get, discoveryDocument.UserInfoEndpoint);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    var response = await httpClient.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("UserInfo endpoint returned non-success status: {status}", response.StatusCode);
                        return null;
                    }
                    var content = await response.Content.ReadAsStringAsync(ct);
                    var claims = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(content);
                    return claims;
                },
                cancellationToken);
        }



    }
}