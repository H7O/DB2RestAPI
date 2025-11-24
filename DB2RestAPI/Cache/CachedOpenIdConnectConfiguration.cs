using Microsoft.IdentityModel.Tokens;

namespace DBToRestAPI.Cache
{
    /// <summary>
    /// Serializable wrapper for OpenIdConnectConfiguration that preserves signing keys across cache storage.
    /// Required because Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration 
    /// doesn't properly serialize/deserialize its SigningKeys collection with HybridCache.
    /// </summary>
    public class CachedOpenIdConnectConfiguration
    {
        public string Issuer { get; set; } = string.Empty;
        public string JwksUri { get; set; } = string.Empty;
        public string AuthorizationEndpoint { get; set; } = string.Empty;
        public string TokenEndpoint { get; set; } = string.Empty;
        public string UserInfoEndpoint { get; set; } = string.Empty;
        public string EndSessionEndpoint { get; set; } = string.Empty;

        /// <summary>
        /// Serialized JSON Web Key Set (JWKS) as JSON string.
        /// Stored as string because JsonWebKeySet doesn't serialize properly through HybridCache.
        /// </summary>
        public string JsonWebKeySetJson { get; set; } = string.Empty;

        /// <summary>
        /// Creates a CachedOpenIdConnectConfiguration from an OpenIdConnectConfiguration and JWKS JSON.
        /// </summary>
        public static CachedOpenIdConnectConfiguration FromDiscoveryDocument(
            Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration config,
            string jwksJson)
        {
            return new CachedOpenIdConnectConfiguration
            {
                Issuer = config.Issuer ?? string.Empty,
                JwksUri = config.JwksUri ?? string.Empty,
                AuthorizationEndpoint = config.AuthorizationEndpoint ?? string.Empty,
                TokenEndpoint = config.TokenEndpoint ?? string.Empty,
                UserInfoEndpoint = config.UserInfoEndpoint ?? string.Empty,
                EndSessionEndpoint = config.EndSessionEndpoint ?? string.Empty,
                JsonWebKeySetJson = jwksJson
            };
        }

        /// <summary>
        /// Converts back to OpenIdConnectConfiguration with properly populated SigningKeys.
        /// </summary>
        public Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration ToDiscoveryDocument()
        {
            var config = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
            {
                Issuer = Issuer,
                JwksUri = JwksUri,
                AuthorizationEndpoint = AuthorizationEndpoint,
                TokenEndpoint = TokenEndpoint,
                UserInfoEndpoint = UserInfoEndpoint,
                EndSessionEndpoint = EndSessionEndpoint
            };

            // Parse and populate signing keys from stored JWKS JSON
            if (!string.IsNullOrWhiteSpace(JsonWebKeySetJson))
            {
                var jwks = new JsonWebKeySet(JsonWebKeySetJson);
                config.JsonWebKeySet = jwks;

                // Manually populate SigningKeys collection (this is what was missing!)
                foreach (var key in jwks.GetSigningKeys())
                {
                    config.SigningKeys.Add(key);
                }
            }

            return config;
        }
    }
}
