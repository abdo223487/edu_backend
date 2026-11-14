namespace EduApi.Services;

/// <summary>
/// Bound from the "CodeRunner" section in appsettings.json (or env vars
/// CodeRunner__BaseUrl, CodeRunner__ApiKey, ...).
///
/// The student code editor sends the source code to POST api/CodeRunner/run and
/// this API forwards it to a Judge0 server (https://judge0.com).
///   - Public instance (testing):   BaseUrl = https://ce.judge0.com   (no key)
///   - Judge0 Cloud / Sulu / Rapid: put the URL they give you in BaseUrl and the
///                                  key in ApiKey (+ ApiKeyHeader / ExtraHeaders).
///   - Self-hosted Judge0:          BaseUrl = http://your-server:2358
/// </summary>
public class CodeRunnerOptions
{
    public const string SectionName = "CodeRunner";

    /// <summary>Base URL of Judge0, WITHOUT a trailing slash or /submissions.</summary>
    public string BaseUrl { get; set; } = "https://ce.judge0.com";

    /// <summary>Optional API key. Leave empty for the public ce.judge0.com.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Header that carries ApiKey (Judge0 default: X-Auth-Token).</summary>
    public string ApiKeyHeader { get; set; } = "X-Auth-Token";

    /// <summary>
    /// Extra headers some providers need, e.g. for RapidAPI:
    /// { "X-RapidAPI-Host": "judge0-ce.p.rapidapi.com" } with ApiKeyHeader = "X-RapidAPI-Key".
    /// </summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();

    /// <summary>Optional override of Judge0 language ids: { "dart": 90 }.</summary>
    public Dictionary<string, int> LanguageIds { get; set; } = new();

    /// <summary>CPU time limit for the program (ms).</summary>
    public int RunTimeoutMs { get; set; } = 3000;

    /// <summary>Wall-clock limit for the program (ms).</summary>
    public int WallTimeoutMs { get; set; } = 8000;

    /// <summary>Max source size accepted from the app (characters).</summary>
    public int MaxCodeLength { get; set; } = 50_000;

    /// <summary>How many runs one student may do per minute.</summary>
    public int MaxRunsPerMinute { get; set; } = 10;
}
