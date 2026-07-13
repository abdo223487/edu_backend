namespace EduApi.Services;

/// <summary>
/// Config for the Redis cache, bound from the "Redis" section of
/// appsettings.json. Used to cache list/GET endpoint responses
/// (Students, Groups) per-tenant.
/// </summary>
public class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>Whether caching is actually turned on.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// StackExchange.Redis connection string, e.g. "my-redis-host:6379,password=xxx"
    /// (Render/Upstash/Redis Cloud all give you one of these directly).
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
