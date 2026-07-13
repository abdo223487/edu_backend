using System.Text;
using System.Text.Json;
using EduApi.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace EduApi.Services;

/// <summary>
/// Sends attendance notifications via Green API (green-api.com) — a normal
/// WhatsApp number linked by QR code, driven through a plain REST endpoint.
/// Sends free-form text (no approved template needed, unlike Meta's Cloud API).
/// </summary>
public class GreenApiWhatsAppService : IWhatsAppService
{
    private readonly HttpClient _http;
    private readonly GreenApiOptions _options;
    private readonly ILogger<GreenApiWhatsAppService> _logger;

    public GreenApiWhatsAppService(IHttpClientFactory httpClientFactory, IOptions<GreenApiOptions> options, ILogger<GreenApiWhatsAppService> logger)
    {
        _http = httpClientFactory.CreateClient("GreenApi");
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendAttendanceNotificationAsync(string parentPhoneNumber, AttendanceWhatsAppNotification data)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("WhatsApp notifications disabled (GreenApi:Enabled=false); skipping.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(parentPhoneNumber))
        {
            _logger.LogWarning("Skipping WhatsApp attendance notification: student has no parent phone number.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.IdInstance) || string.IsNullOrWhiteSpace(_options.ApiTokenInstance))
        {
            _logger.LogWarning("GreenApi:IdInstance / ApiTokenInstance not configured; skipping notification.");
            return false;
        }

        var chatId = NormalizePhoneNumber(parentPhoneNumber);
        if (chatId == null)
        {
            _logger.LogWarning("Skipping WhatsApp attendance notification: could not normalize phone number '{Phone}'.", parentPhoneNumber);
            return false;
        }

        var messageText = BuildMessageText(data);

        var payload = new { chatId, message = messageText };

        try
        {
            var url = $"{_options.ApiUrl.TrimEnd('/')}/waInstance{_options.IdInstance}/sendMessage/{_options.ApiTokenInstance}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Green API send failed ({Status}) for {ChatId}: {Body}", response.StatusCode, chatId, body);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // NEVER let a WhatsApp failure bubble up and break attendance recording.
            _logger.LogWarning(ex, "Green API send threw for {ChatId}.", chatId);
            return false;
        }
    }

    private static string BuildMessageText(AttendanceWhatsAppNotification data)
    {
        return
            $"تم تسجيل حضور الطالب/ة {data.StudentName} مع المدرس {data.TeacherName} " +
            $"بتاريخ {data.AttendanceLocalTime:dd/MM/yyyy} الساعة {data.AttendanceLocalTime:HH:mm:ss}.\n\n" +
            $"آخر درجة: {data.LastGradeText}\n" +
            $"آخر واجب: {data.LastHomeworkText}\n" +
            $"حالة المذكرة: {data.NotebookStatusText}";
    }

    /// <summary>
    /// Green API expects a "chatId" in the form "{countryCode}{number}@c.us"
    /// (no "+", no leading 0). Egyptian parent numbers are almost always
    /// stored locally as "01012345678" (11 digits) — convert those to
    /// "20" + the number without the leading 0.
    /// </summary>
    private static string? NormalizePhoneNumber(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        if (digits.StartsWith("0") && digits.Length == 11)
            return "2" + digits + "@c.us"; // 01XXXXXXXXX -> 201XXXXXXXXX

        if (digits.StartsWith("20"))
            return digits + "@c.us";

        // Fallback: assume it's already in international form.
        return digits + "@c.us";
    }
}
