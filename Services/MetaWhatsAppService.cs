using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EduApi.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace EduApi.Services;

/// <summary>
/// Sends attendance notifications via Meta's WhatsApp Cloud API
/// (https://graph.facebook.com/{version}/{phoneNumberId}/messages), using the
/// approved "attendance_notification" template (named body variables — see
/// WhatsAppOptions for the exact shape).
/// </summary>
public class MetaWhatsAppService : IWhatsAppService
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<MetaWhatsAppService> _logger;

    public MetaWhatsAppService(IHttpClientFactory httpClientFactory, IOptions<WhatsAppOptions> options, ILogger<MetaWhatsAppService> logger)
    {
        _http = httpClientFactory.CreateClient("WhatsApp");
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendAttendanceNotificationAsync(string parentPhoneNumber, AttendanceWhatsAppNotification data)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("WhatsApp notifications disabled (WhatsApp:Enabled=false); skipping.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(parentPhoneNumber))
        {
            _logger.LogWarning("Skipping WhatsApp attendance notification: student has no parent phone number.");
            return false;
        }

        var egyptTime = ToEgyptTime(data.AttendanceLocalTime);

        var parameters = new object[]
        {
            new { type = "text", parameter_name = "student_name", text = data.StudentName },
            new { type = "text", parameter_name = "teacher_name", text = data.TeacherName },
            new { type = "text", parameter_name = "date", text = egyptTime.ToString("dd/MM/yyyy") },
            new { type = "text", parameter_name = "time", text = egyptTime.ToString("HH:mm:ss") },
            new { type = "text", parameter_name = "last_grade", text = data.LastGradeText },
            new { type = "text", parameter_name = "last_homework", text = data.LastHomeworkText },
            new { type = "text", parameter_name = "notebook_status", text = data.NotebookStatusText },
        };

        return await SendTemplateAsync(parentPhoneNumber, _options.TemplateName, parameters, "attendance");
    }

    public async Task<bool> SendWelcomeMessageAsync(string studentPhoneNumber, StudentWelcomeWhatsAppNotification data)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("WhatsApp notifications disabled (WhatsApp:Enabled=false); skipping.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(studentPhoneNumber))
        {
            _logger.LogWarning("Skipping WhatsApp welcome message: student has no phone number.");
            return false;
        }

        var parameters = new object[]
        {
            new { type = "text", parameter_name = "student_name", text = data.StudentName },
            new { type = "text", parameter_name = "username", text = data.UserName },
            new { type = "text", parameter_name = "password", text = data.Password },
        };

        return await SendTemplateAsync(studentPhoneNumber, _options.WelcomeTemplateName, parameters, "welcome");
    }

    public async Task<bool> SendDismissalNotificationAsync(string parentPhoneNumber, DismissalWhatsAppNotification data)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("WhatsApp notifications disabled (WhatsApp:Enabled=false); skipping.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(parentPhoneNumber))
        {
            _logger.LogWarning("Skipping WhatsApp dismissal notification: no parent phone number.");
            return false;
        }

        var egyptTime = ToEgyptTime(data.DismissalLocalTime);

        var parameters = new object[]
        {
            new { type = "text", parameter_name = "teacher_name", text = data.TeacherName },
            new { type = "text", parameter_name = "lesson_title", text = data.LessonTitle },
            new { type = "text", parameter_name = "group_name", text = data.GroupName },
            new { type = "text", parameter_name = "date", text = egyptTime.ToString("dd/MM/yyyy") },
            new { type = "text", parameter_name = "time", text = egyptTime.ToString("HH:mm") },
        };

        return await SendTemplateAsync(parentPhoneNumber, _options.DismissalTemplateName, parameters, "dismissal");
    }

    private async Task<bool> SendTemplateAsync(string rawPhoneNumber, string templateName, object[] bodyParameters, string kind)
    {
        if (string.IsNullOrWhiteSpace(_options.PhoneNumberId) || string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            _logger.LogWarning("WhatsApp:PhoneNumberId / AccessToken not configured; skipping {Kind} notification.", kind);
            return false;
        }

        var to = NormalizePhoneNumber(rawPhoneNumber);
        if (to == null)
        {
            _logger.LogWarning("Skipping WhatsApp {Kind} notification: could not normalize phone number '{Phone}'.", kind, rawPhoneNumber);
            return false;
        }

        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = _options.TemplateLanguageCode },
                components = new object[]
                {
                    new { type = "body", parameters = bodyParameters }
                }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiVersion}/{_options.PhoneNumberId}/messages")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("WhatsApp {Kind} send failed ({Status}) for {Phone}: {Body}", kind, response.StatusCode, to, body);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // NEVER let a WhatsApp failure bubble up and break the caller's flow.
            _logger.LogWarning(ex, "WhatsApp {Kind} send threw for {Phone}.", kind, to);
            return false;
        }
    }

    /// <summary>
    /// Meta's API expects the number in international format without a
    /// leading "+" (e.g. "201012345678"). Egyptian parent numbers are almost
    /// always stored locally as "01012345678" (11 digits) — convert those to
    /// "20" + the number without the leading 0. Numbers already given with a
    /// country code are passed through (digits only).
    /// </summary>
    /// <summary>
    /// Converts a UTC (or unspecified-but-actually-UTC) DateTime coming from
    /// the Flutter client into Egypt local time (UTC+2, or UTC+3 during any
    /// future DST rules) for display in the WhatsApp message. Falls back to
    /// a fixed +2h offset if the "Africa/Cairo" IANA zone isn't available on
    /// the host (e.g. minimal Linux containers without tzdata).
    /// </summary>
    private static DateTime ToEgyptTime(DateTime value)
    {
        var utcValue = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        try
        {
            var cairoZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
            return TimeZoneInfo.ConvertTimeFromUtc(utcValue, cairoZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return utcValue.AddHours(2);
        }
        catch (InvalidTimeZoneException)
        {
            return utcValue.AddHours(2);
        }
    }

    private static string? NormalizePhoneNumber(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        if (digits.StartsWith("0") && digits.Length == 11)
            return "2" + digits; // 01XXXXXXXXX -> 201XXXXXXXXX

        if (digits.StartsWith("20"))
            return digits;

        // Fallback: assume it's already in international form.
        return digits;
    }
}
