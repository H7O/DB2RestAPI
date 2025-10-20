# API Gateway Cache Implementation

## Overview
This document describes the caching implementation for API Gateway routes in DB2RestAPI.

## Changes Made

### 1. **CachableHttpResponseContainer.cs** - Enhanced
- ✅ Added `StatusCode` property to store HTTP status code
- ✅ Updated `Parse()` method to capture status code from response

### 2. **CacheService.cs** - New Gateway Cache Methods
- ✅ Added `GetForGateway<T>()` method specifically for API gateway caching
- ✅ Added `GetCacheInfoForGateway()` private method that builds cache keys from:
  - HTTP method (GET, POST, PUT, DELETE, etc.)
  - Resolved route path (after wildcard matching)
  - Query string parameters (from caller, not hardcoded in URL)
  - Request headers
  - Configured invalidators

### 3. **Step4APIGatewayProcess.cs** - Refactored with Caching
- ✅ Refactored gateway processing logic into `ProcessApiGatewayRequestAsync()` method
- ✅ Added cache wrapper that calls `CacheService.GetForGateway()`
- ✅ Implemented dual-mode response handling:
  - **Streaming mode** (`disableStreaming=false`): Streams response directly to client, returns `null`
  - **Buffered mode** (`disableStreaming=true`): Buffers entire response for caching, returns `CachableHttpResponseContainer`
- ✅ Added `WriteHttpResponseFromCache()` method to serve cached responses
- ✅ Added support for `exclude_status_codes_from_cache` configuration

## Configuration

### Cache Configuration in `api_gateway.xml`

```xml
<hello_world_loopback_with_cache>
  <route>loopback/hello_world_with_cache</route>
  <url>https://localhost:7054/hello_world</url>
  <excluded_headers>x-api-key,host</excluded_headers>

  <cache>
    <memory>
      <!-- Cache duration in milliseconds -->
      <duration_in_milliseconds>20000</duration_in_milliseconds>
      
      <!-- Parameters that invalidate cache (comma-separated) -->
      <!-- Looks in both query parameters and headers -->
      <invalidators>name</invalidators>
      
      <!-- Optional: Don't cache these status codes (comma-separated) -->
      <exclude_status_codes_from_cache>401,403,429</exclude_status_codes_from_cache>
      
      <!-- Optional: Max size per invalidator value (default: 1000) -->
      <max_per_value_cache_size>1000</max_per_value_cache_size>
    </memory>
  </cache>
</hello_world_loopback_with_cache>
```

## Features

### ✅ Intelligent Cache Key Generation
Cache keys include:
- Section key (route configuration name)
- HTTP method
- Resolved route path
- Query parameters (only those specified in `invalidators`)
- Headers (only those specified in `invalidators`)

### ✅ Streaming vs Buffering
- **No cache configured**: Streams directly (best performance)
- **Cache configured**: Buffers response in memory, then caches
- **Status code excluded**: Falls back to streaming even if cache configured

### ✅ Status Code Filtering
Administrators can exclude specific status codes from caching:
```xml
<exclude_status_codes_from_cache>401,403,429,500,502,503</exclude_status_codes_from_cache>
```

**Default behavior**: All status codes are cached (including errors like 404, 500, etc.)
- This protects downstream services from high traffic during outages
- Admins can opt-out specific codes they don't want cached

### ✅ Wildcard Route Support
Cache keys respect wildcard routes:
- Route config: `cat/*`
- Request: `cat/facts/list`
- Cache key includes: `cat/facts/list` (the resolved path)

## Testing

### Test Endpoint Configuration
The `hello_world_loopback_with_cache` route is configured for testing:
- Calls back to the same service at `https://localhost:7054/hello_world`
- Returns timestamp in response (perfect for verifying caching)
- Cache duration: 20 seconds
- Invalidator: `name` parameter

### Test Commands

#### Test 1: Basic caching (same name)
```powershell
# First call - should hit the database, cache the response
curl -X GET "https://localhost:7054/loopback/hello_world_with_cache?name=test"

# Wait 1-2 seconds, then call again
curl -X GET "https://localhost:7054/loopback/hello_world_with_cache?name=test"
```
**Expected**: Same timestamp in both responses (cached)

#### Test 2: Cache invalidation (different name)
```powershell
# Call with name=test
curl -X GET "https://localhost:7054/loopback/hello_world_with_cache?name=test"

# Call with name=john (different invalidator)
curl -X GET "https://localhost:7054/loopback/hello_world_with_cache?name=john"
```
**Expected**: Different timestamps (different cache entries)

#### Test 3: Cache expiration
```powershell
# First call
curl -X GET "https://localhost:7054/loopback/hello_world_with_cache?name=test"

# Wait 21+ seconds (cache duration is 20 seconds)
Start-Sleep -Seconds 21

# Call again
curl -X GET "https://localhost:7054/loopback/hello_world_with_cache?name=test"
```
**Expected**: Different timestamps (cache expired)

#### Test 4: No cache (different route)
```powershell
# Call the regular loopback (no cache configured)
curl -X GET "https://localhost:7054/loopback/hello_world?name=test"

# Call again immediately
curl -X GET "https://localhost:7054/loopback/hello_world?name=test"
```
**Expected**: Different timestamps (no caching, streaming mode)

#### Test 5: HTTP Method distinction
```powershell
# GET request
curl -X GET "https://localhost:7054/loopback/hello_world_with_cache?name=test"

# POST request (same parameters)
curl -X POST "https://localhost:7054/loopback/hello_world_with_cache?name=test"
```
**Expected**: Different timestamps (different cache entries per method)

## Architecture Decisions

### Why Cache Everything by Default?
- Protects downstream services during high traffic
- Reduces load on target APIs
- Error responses (404, 500) are often valid temporary states
- Admins can opt-out specific codes via `exclude_status_codes_from_cache`

### Why Include HTTP Method in Cache Key?
- Same endpoint can behave differently for GET vs POST
- Prevents cache pollution between read and write operations
- Provides granular control for API administrators

### Why Not Parse Request Bodies?
- **Phase 1 approach**: Keep it simple, safe, and fast
- Maintains streaming architecture integrity
- No security risk from body parsing
- Can be added later if truly needed (Phase 2)

### Why Use `CachableHttpResponseContainer` Instead of `ObjectResult`?
- Needs to store HTTP headers and status code
- `ObjectResult` is MVC-specific and doesn't fit proxy scenarios
- Custom container allows precise control over cached data

## Performance Impact

### Before Caching
- Every request proxied to target API
- Response streamed directly (optimal for single requests)

### After Caching (Configured)
- First request: Buffered in memory, then cached
- Subsequent requests: Served from cache (much faster)
- Expired/invalidated: Falls back to buffering + caching

### After Caching (Not Configured)
- **No change**: Still streams directly (maintains backward compatibility)

## Future Enhancements (Not Implemented)

### Phase 2 Ideas:
- Optional request body hashing for cache key
- Distributed cache support (Redis, SQL Server)
- Cache statistics/monitoring endpoint
- Cache warming strategies
- Conditional caching based on response headers (e.g., Cache-Control)

## Notes

- ✅ Fully backward compatible (existing routes without cache work unchanged)
- ✅ No breaking changes to existing functionality
- ✅ Follows same patterns as database query caching
- ✅ Thread-safe (HybridCache handles concurrency)
- ✅ Configuration-driven (no code changes needed for new routes)
