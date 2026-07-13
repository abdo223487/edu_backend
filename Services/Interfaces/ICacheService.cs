namespace EduApi.Services.Interfaces;

/// <summary>
/// Thin caching layer over Redis, used to cache the response of expensive/
/// frequently-hit read (GET) endpoints — e.g. Students and Groups lists —
/// so repeated requests for the same tenant + query don't have to re-run
/// the underlying EF Core query every time.
///
/// Contract: NEVER throws. If Redis is unreachable/misconfigured, every
/// method here degrades to "no cache" (GetOrCreateAsync just calls the
/// factory directly) rather than taking the API down — Redis is a
/// performance optimization here, not a source of truth.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Returns the cached value for <paramref name="key"/> if present;
    /// otherwise calls <paramref name="factory"/>, caches the result for
    /// <paramref name="ttl"/>, and returns it.
    /// </summary>
    Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory);

    /// <summary>
    /// Removes every cached key starting with <paramref name="prefix"/>.
    /// Used to invalidate a tenant's cached lists after a write (e.g. a new
    /// Student is added -> wipe "tenant:{id}:students:*").
    /// </summary>
    Task RemoveByPrefixAsync(string prefix);
}
