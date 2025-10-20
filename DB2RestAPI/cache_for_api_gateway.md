## My Recommended Approach: **Phased Implementation**

### **Phase 1: MVP - Query String + Headers Only** (Start Here)
```
Cache Key = SHA256(method + path + selected_query_params + selected_headers)
Body = streamed through (not cached, not inspected)
```

**Rationale:**
- 80% of cacheable API calls are GET requests (no body anyway)
- Keeps your streaming architecture intact
- Zero security risk from body parsing
- Simple, fast, predictable

**Config Example:**
```json
{
  "route": "/api/users",
  "cache": {
    "enabled": true,
    "ttl": "5m",
    "invalidators": ["userId", "tenantId"],  // from query/headers only
    "methods": ["GET"]  // Only cache GETs initially
  }
}
```

---

### **Phase 2: Optional Body Hashing** (If Needed Later)
```
Cache Key = SHA256(method + path + params + headers + body_hash)
Body = stream to temp → hash → stream to target
```

**Implementation Sketch:**
```csharp
if (routeConfig.Cache.IncludeBodyInKey && context.Request.ContentLength.HasValue)
{
    // Guard: Max body size check
    if (context.Request.ContentLength.Value > routeConfig.Cache.MaxBodySizeForCaching)
    {
        // Skip caching, just proxy through
        await ProxyRequestWithoutCaching(context);
        return;
    }
    
    // Stream to temp file + calculate hash
    var tempFile = Path.GetTempFileName();
    using var fileStream = File.OpenWrite(tempFile);
    using var sha256 = SHA256.Create();
    using var cryptoStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write);
    
    await context.Request.Body.CopyToAsync(cryptoStream);
    cryptoStream.FlushFinalBlock();
    
    var bodyHash = Convert.ToBase64String(sha256.Hash);
    cacheKey += $"_body:{bodyHash}";
    
    // Now stream temp file to target API
    using var readStream = File.OpenRead(tempFile);
    // ... proxy to target
}
```

---

## **My Recommendation: Start with Phase 1**

Here's why:

### ✅ **Go Simple First**
1. **Most APIs that need caching are read-heavy** (GET requests with query params)
2. Your streaming architecture is a **strength** - don't compromise it unnecessarily
3. Body parsing is a **large attack surface** - avoid unless truly needed

### 🛡️ **Layered Protection Strategy**
Even if you add body hashing later, protect at multiple levels:

```json
{
  "global": {
    "maxRequestSize": "10MB"  // IIS/Kestrel level
  },
  "route": {
    "cache": {
      "maxBodySizeForCaching": "1MB",  // Only cache small bodies
      "enabled": true
    }
  }
}
```

### 📊 **Decision Tree for Caching**
```
Is method GET/HEAD?
  └─ Yes → Cache based on query string + headers ✅
  └─ No → Is POST/PUT/PATCH?
      └─ Yes → Has body?
          └─ Yes → Body size < maxBodySizeForCaching?
              └─ Yes → Hash body + cache ✅
              └─ No → Skip cache, proxy through 🚫
          └─ No → Cache based on headers only ✅
```

---

## **Concrete Implementation Plan**

### **Option A: Conservative (Recommended)**
```
✅ Cache GET requests only (query + headers)
✅ Stream everything else through
✅ Simple, safe, covers 80% of use cases
⏸️ Defer POST/PUT caching until proven need
```

### **Option B: Moderate**
```
✅ Cache GET requests (query + headers)
✅ Cache POST/PUT/PATCH if body < 1MB (body hash)
⚠️ Stream bodies > 1MB through without caching
✅ Admin configures max body size per route
```

### **Option C: Advanced (Not Recommended Yet)**
```
❌ Parse JSON/XML bodies for field extraction
❌ Too complex, too risky
❌ Save for v2.0 if customers demand it
```

---

## **Sample Configuration Schema**
```xml
    <cat_facts_custom_path_example>
      <route>cat/facts/list</route>
      <url>https://catfact.ninja/fact</url>
      <excluded_headers>x-api-key,host</excluded_headers>
      <cache>
        <memory>
          <duration_in_milliseconds>20000</duration_in_milliseconds>
          <invalidators>name</invalidators>
        </memory>
      </cache>
    </cat_facts_custom_path_example>
```

the below is just an example of naming conventions I might adopt when finalizing the engine.

```json
{
  "routes": [
    {
      "path": "/api/products",
      "target": "https://backend.api.com",
      "cache": {
        "enabled": true,
        "ttl": "5m",
        "methods": ["GET"],  // Only cache GETs initially
        "invalidators": {
          "queryParams": ["category", "page"],
          "headers": ["X-Tenant-Id"]
          // NO body invalidators in Phase 1
        }
      }
    },
    {
      "path": "/api/orders",
      "target": "https://backend.api.com",
      "cache": {
        "enabled": true,
        "ttl": "1m",
        "methods": ["GET", "POST"],
        "includeBodyInKey": true,  // Phase 2 feature
        "maxBodySizeForCaching": "500KB",
        "invalidators": {
          "queryParams": ["orderId"],
          "headers": ["Authorization"]
        }
      }
    }
  ]
}
```

---

## **My Bottom Line Recommendation**

**Start with Option A (GET requests only, query + headers)**

Why?
- ✅ Solves the common case (read-heavy APIs)
- ✅ Zero security risk
- ✅ Preserves your elegant streaming architecture
- ✅ You can ship it **this week**
- ✅ Gather real usage data before complicating

**Then:**
- Monitor cache hit rates
- Listen to actual customer needs
- Add body hashing (Option B) **only if** you see clear demand

**Key insight:** Most API gateways (Nginx, Kong, AWS API Gateway) start by caching GET requests only. POST/PUT caching is rare in practice because those are usually mutation operations, not reads.