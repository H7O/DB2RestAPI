namespace DB2RestAPI.Cache
{
    public class CachedUserInfo
    {
        public Dictionary<string, object> Claims { get; set; } = new();
        public DateTime ExpiresAt { get; set; }
    }
}
