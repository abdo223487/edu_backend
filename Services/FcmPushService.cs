using EduApi.Data;
using EduApi.Services.Interfaces;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EduApi.Services;

/// <summary>
/// FCM implementation of IPushService (Firebase Admin SDK, HTTP v1 under the hood).
/// The FirebaseApp is created once per process; if credentials are missing or
/// invalid the service just logs a warning once and behaves as a no-op.
/// Tokens FCM reports as unregistered/invalid are deleted automatically.
/// </summary>
public class FcmPushService : IPushService
{
    private static readonly object InitLock = new();
    private static bool _initTried;
    private static bool _ready;

    private readonly AppDbContext _db;
    private readonly ILogger<FcmPushService> _log;

    public FcmPushService(AppDbContext db, IOptions<FcmOptions> options, ILogger<FcmPushService> log)
    {
        _db = db;
        _log = log;
        EnsureInitialized(options.Value, log);
    }

    public bool Enabled => _ready;

    private static void EnsureInitialized(FcmOptions o, ILogger log)
    {
        if (_initTried) return;
        lock (InitLock)
        {
            if (_initTried) return;
            _initTried = true;

            if (!o.Enabled)
            {
                log.LogInformation("FCM push disabled (Fcm:Enabled=false).");
                return;
            }

            try
            {
                var json = o.ServiceAccountJson?.Trim() ?? string.Empty;
                if (json.Length == 0 && !string.IsNullOrWhiteSpace(o.ServiceAccountPath) && File.Exists(o.ServiceAccountPath))
                    json = File.ReadAllText(o.ServiceAccountPath);

                if (json.Length == 0)
                {
                    log.LogWarning("FCM enabled but no service account configured (Fcm:ServiceAccountJson / Fcm:ServiceAccountPath). Push is a no-op.");
                    return;
                }

                // Allow base64-encoded JSON (easier to paste into some hosting dashboards).
                if (!json.StartsWith("{"))
                    json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(json));

                if (FirebaseApp.DefaultInstance == null)
                {
                    FirebaseApp.Create(new AppOptions { Credential = GoogleCredential.FromJson(json) });
                }
                _ready = true;
                log.LogInformation("FCM push initialized.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to initialize FCM. Push is a no-op.");
            }
        }
    }

    public Task SendToStudentsAsync(IEnumerable<int> studentIds, string title, string body,
        Dictionary<string, string>? data = null, CancellationToken ct = default)
        => SendAsync("Student", studentIds, title, body, data, ct);

    public Task SendToStaffAsync(IEnumerable<int> staffIds, string title, string body,
        Dictionary<string, string>? data = null, CancellationToken ct = default)
        => SendAsync("Staff", staffIds, title, body, data, ct);

    private async Task SendAsync(string userType, IEnumerable<int> userIds, string title, string body,
        Dictionary<string, string>? data, CancellationToken ct)
    {
        if (!_ready) return;

        try
        {
            var ids = userIds.Distinct().ToList();
            if (ids.Count == 0) return;

            var tokens = await _db.DeviceTokens.AsNoTracking()
                .Where(d => d.UserType == userType && ids.Contains(d.UserId))
                .Select(d => d.Token)
                .Distinct()
                .ToListAsync(ct);
            if (tokens.Count == 0) return;

            // FCM data payload values must all be strings.
            var payload = data != null
                ? new Dictionary<string, string>(data)
                : new Dictionary<string, string>();

            var dead = new List<string>();

            // FCM multicast limit = 500 tokens per call.
            foreach (var chunk in tokens.Chunk(500))
            {
                var message = new MulticastMessage
                {
                    Tokens = chunk.ToList(),
                    Notification = new FirebaseAdmin.Messaging.Notification { Title = title, Body = body },
                    Data = payload,
                    Android = new AndroidConfig
                    {
                        Priority = Priority.High,
                        Notification = new AndroidNotification { ChannelId = "default_channel" }
                    },
                    Apns = new ApnsConfig
                    {
                        Aps = new Aps { Sound = "default" }
                    }
                };

                var response = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(message, ct);
                for (var i = 0; i < response.Responses.Count; i++)
                {
                    var r = response.Responses[i];
                    if (r.IsSuccess) continue;
                    var code = r.Exception?.MessagingErrorCode;
                    if (code == MessagingErrorCode.Unregistered || code == MessagingErrorCode.InvalidArgument)
                        dead.Add(chunk[i]);
                }
            }

            if (dead.Count > 0)
            {
                await _db.DeviceTokens.Where(d => dead.Contains(d.Token)).ExecuteDeleteAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // Best effort: never bubble a push failure up to the caller.
            _log.LogWarning(ex, "Push send failed ({UserType}).", userType);
        }
    }
}
