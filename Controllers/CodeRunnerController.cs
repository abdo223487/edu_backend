using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EduApi.Common;
using EduApi.Models;
using EduApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/CodeRunner  (student only)
///  GET  CodeRunner/languages   the languages the code editor offers
///  POST CodeRunner/run         { language, code, stdin? } -> compiles/runs the code in a sandbox
///
/// The code is NOT executed on this server: it is forwarded to a sandboxed Judge0
/// engine (see CodeRunnerOptions). This controller validates the request, applies a
/// per-student rate limit and converts Judge0's answer to the format the app expects.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class CodeRunnerController : ControllerBase
{
    // App language id -> Judge0 language id (taken from https://ce.judge0.com/languages).
    // Can be overridden from config: CodeRunner:LanguageIds:dart = 90
    private static readonly Dictionary<string, int> DefaultLanguageIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = 109,      // Python (3.13.2)
        ["cpp"] = 105,         // C++ (GCC 14.1.0)
        ["c"] = 103,           // C (GCC 14.1.0)
        ["java"] = 91,         // Java (JDK 17.0.6)  -> the public class must be named Main
        ["javascript"] = 102,  // JavaScript (Node.js 22)
        ["csharp"] = 51,       // C# (Mono 6.6.0.161)
        ["dart"] = 90,         // Dart (2.19.2)
    };

    // userId -> timestamps of recent runs (sliding 60s window).
    private static readonly ConcurrentDictionary<int, Queue<DateTime>> RecentRuns = new();

    private const int MaxOutputChars = 20_000;
    private const int MaxStdinChars = 10_000;
    private const int PollAttempts = 15;
    private const int PollDelayMs = 700;

    private readonly IHttpClientFactory _http;
    private readonly CodeRunnerOptions _opt;
    private readonly ILogger<CodeRunnerController> _log;

    public CodeRunnerController(
        IHttpClientFactory http,
        IOptions<CodeRunnerOptions> opt,
        ILogger<CodeRunnerController> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public class RunRequest
    {
        public string Language { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? Stdin { get; set; }
    }

    [HttpGet("languages")]
    public IActionResult GetLanguages() => Ok(DefaultLanguageIds.Keys.ToArray());

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] RunRequest req, CancellationToken ct)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(new { message = "اكتب الكود الأول." });

        var langKey = (req.Language ?? string.Empty).Trim();
        if (!TryGetLanguageId(langKey, out var languageId))
            return BadRequest(new { message = "لغة البرمجة غير مدعومة." });

        if (req.Code.Length > _opt.MaxCodeLength)
            return BadRequest(new { message = "الكود طويل جدًا." });

        var stdin = req.Stdin ?? string.Empty;
        if (stdin.Length > MaxStdinChars) stdin = stdin[..MaxStdinChars];

        if (!TryAcquireRunSlot(User.GetUserId()))
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = "كتر عدد مرات التشغيل. استنى شوية وجرب تاني." });

        var payload = new JsonObject
        {
            ["language_id"] = languageId,
            ["source_code"] = ToB64(req.Code),
            ["cpu_time_limit"] = Math.Max(1, _opt.RunTimeoutMs / 1000.0),
            ["wall_time_limit"] = Math.Max(2, _opt.WallTimeoutMs / 1000.0),
        };
        if (stdin.Length > 0) payload["stdin"] = ToB64(stdin);

        try
        {
            var client = _http.CreateClient("CodeRunner");

            using var post = NewRequest(HttpMethod.Post, "submissions?base64_encoded=true&wait=true");
            post.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var res = await client.SendAsync(post, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return Ok(Error("الخدمة مشغولة دلوقتي. جرب بعد شوية."));

            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("Judge0 returned {Status}: {Body}", (int)res.StatusCode, Trunc(body, 500));
                return Ok(Error("خدمة تشغيل الأكواد مش متاحة دلوقتي. جرب بعد شوية."));
            }

            var root = SafeParse(body);
            if (root == null) return Ok(Error("رد غير متوقع من خدمة التشغيل."));

            // If wait=true is not honoured the answer is only a token (or status 1/2): poll for it.
            var token = Str(root["token"]);
            var statusId = (int?)Num(root["status"]?["id"]);
            for (var i = 0; (statusId == null || statusId < 3) && !string.IsNullOrEmpty(token) && i < PollAttempts; i++)
            {
                await Task.Delay(PollDelayMs, ct);
                using var get = NewRequest(HttpMethod.Get,
                    $"submissions/{Uri.EscapeDataString(token)}?base64_encoded=true&fields=*");
                using var pres = await client.SendAsync(get, ct);
                var pbody = await pres.Content.ReadAsStringAsync(ct);
                if (!pres.IsSuccessStatusCode) continue;
                var pr = SafeParse(pbody);
                if (pr == null) continue;
                root = pr;
                statusId = (int?)Num(root["status"]?["id"]);
            }

            if (statusId == null || statusId < 3)
                return Ok(Error("التشغيل خد وقت طويل. جرب تاني."));

            return Ok(Normalize(root, statusId.Value));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (TaskCanceledException)
        {
            return Ok(Error("التشغيل خد وقت طويل. جرب تاني."));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CodeRunner failed");
            return Ok(Error("حصلت مشكلة أثناء تشغيل الكود."));
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private bool TryGetLanguageId(string key, out int id)
    {
        foreach (var kv in _opt.LanguageIds)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && DefaultLanguageIds.ContainsKey(key))
            {
                id = kv.Value;
                return true;
            }
        return DefaultLanguageIds.TryGetValue(key, out id);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var msg = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
            msg.Headers.TryAddWithoutValidation(
                string.IsNullOrWhiteSpace(_opt.ApiKeyHeader) ? "X-Auth-Token" : _opt.ApiKeyHeader.Trim(),
                _opt.ApiKey.Trim());
        foreach (var h in _opt.ExtraHeaders)
            msg.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return msg;
    }

    private bool TryAcquireRunSlot(int userId)
    {
        var q = RecentRuns.GetOrAdd(userId, _ => new Queue<DateTime>());
        lock (q)
        {
            var now = DateTime.UtcNow;
            while (q.Count > 0 && (now - q.Peek()).TotalSeconds > 60) q.Dequeue();
            if (q.Count >= _opt.MaxRunsPerMinute) return false;
            q.Enqueue(now);
            return true;
        }
    }

    private static string ToB64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    /// <summary>Judge0 (base64_encoded=true) returns text fields as base64, or null.</summary>
    private static string FromB64(JsonNode? n)
    {
        var s = Str(n);
        if (s.Length == 0) return string.Empty;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch (FormatException) { return s; }
    }

    private static JsonNode? SafeParse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static object Error(string message) => new
    {
        success = false,
        status = "error",
        stdout = string.Empty,
        stderr = message,
        compileOutput = string.Empty,
        timeMs = (double?)null,
        memoryKb = (double?)null,
        exitCode = (int?)null,
    };

    private static string Trunc(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "\n…");

    private static double? Num(JsonNode? n)
    {
        if (n == null) return null;
        try { return n.GetValue<double>(); } catch { /* maybe a string */ }
        try
        {
            var s = n.GetValue<string>();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        }
        catch { /* ignore */ }
        return null;
    }

    private static string Str(JsonNode? n)
    {
        if (n == null) return string.Empty;
        try { return n.GetValue<string>() ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Judge0 status ids: 3 Accepted, 5 Time Limit, 6 Compilation Error,
    /// 7-12 Runtime Error (SIGSEGV, SIGXFSZ, SIGFPE, SIGABRT, NZEC, Other),
    /// 13 Internal Error, 14 Exec Format Error.
    /// </summary>
    private static object Normalize(JsonNode root, int statusId)
    {
        var stdout = FromB64(root["stdout"]);
        var stderr = FromB64(root["stderr"]);
        var compileOut = FromB64(root["compile_output"]);
        var message = FromB64(root["message"]);

        var timeSec = Num(root["time"]);
        var memKb = Num(root["memory"]);

        string status = statusId switch
        {
            3 => "success",
            6 => "compile_error",
            5 => "timeout",
            >= 7 and <= 12 => "runtime_error",
            14 => "runtime_error",
            _ => "error",
        };

        if (status == "timeout" && string.IsNullOrWhiteSpace(stderr))
            stderr = "Time Limit Exceeded";
        else if (status == "runtime_error" && string.IsNullOrWhiteSpace(stderr))
            stderr = message;
        else if (status == "error" && string.IsNullOrWhiteSpace(stderr))
            stderr = string.IsNullOrWhiteSpace(message) ? "Internal error" : message;

        // Compiler notes (e.g. Dart's "Compiling with sound null safety") are noise unless it failed.
        if (status != "compile_error") compileOut = string.Empty;

        return new
        {
            success = status == "success",
            status,
            stdout = Trunc(stdout, MaxOutputChars),
            stderr = Trunc(stderr, MaxOutputChars),
            compileOutput = Trunc(compileOut, MaxOutputChars),
            timeMs = timeSec.HasValue ? timeSec.Value * 1000.0 : (double?)null,
            memoryKb = memKb,
            exitCode = (int?)null,
        };
    }
}
