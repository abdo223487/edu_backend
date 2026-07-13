using System.Text.Json;
using EduApi.Services.Interfaces;
using StackExchange.Redis;

namespace EduApi.Services;

/// <summary>
/// ICacheService implementation backed by Redis (via StackExchange.Redis).
/// Used directly (instead of going through IDistributedCache) because
/// invalidating a whole family of keys at once — "every cached Students
/// list for tenant 7" — needs SCAN + DEL, which IDistributedCache has no
/// concept of.
/// </summary>
public class RedisCacheService : ICacheService
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(IConnectionMultiplexer? redis, ILogger<RedisCacheService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        if (_redis == null || !_redis.IsConnected)
            return await factory();

        try
        {
            var db = _redis.GetDatabase();
            var cached = await db.StringGetAsync(key);
            if (cached.HasValue)
            {
                var value = JsonSerializer.Deserialize<T>(cached!);
                if (value != null) return value;
            }
        }
        catch (Exception ex)
        {
            // Redis read failed for whatever reason (network blip, etc.) —
            // fall through to computing the value normally.
            _logger.LogWarning(ex, "Redis GET failed for key {Key}; falling back to source.", key);
        }

        var result = await factory();

        try
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync(key, JsonSerializer.Serialize(result), ttl);
        }
        catch (Exception ex)
        {
            // Never let a cache-write failure fail the request — the caller
            // already has a good result, they just won't get it cached.
            _logger.LogWarning(ex, "Redis SET failed for key {Key}.", key);
        }

        return result;
    }

    public async Task RemoveByPrefixAsync(string prefix)
    {
        if (_redis == null || !_redis.IsConnected) return;

        try
        {
            foreach (var endpoint in _redis.GetEndPoints())
            {
                var server = _redis.GetServer(endpoint);
                var db = _redis.GetDatabase();
                // KEYS pattern match via SCAN (non-blocking, safe for production use
                // unlike the raw KEYS command). Prefix sets here are small/per-tenant
                // so this is cheap in practice.
                await foreach (var key in server.KeysAsync(pattern: $"{prefix}*"))
                    await db.KeyDeleteAsync(key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis prefix invalidation failed for prefix {Prefix}.", prefix);
        }
    }
}
