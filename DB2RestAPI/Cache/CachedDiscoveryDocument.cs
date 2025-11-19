namespace DB2RestAPI.Cache
{
    public class CachedDiscoveryDocument
    {
        public Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration Document { get; set; }
        public DateTime RefreshAfter { get; set; }
    }

}
