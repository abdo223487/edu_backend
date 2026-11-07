using EduApi.Common;
using EduApi.Services.Interfaces;

namespace EduApi.Services;

/// <summary>
/// "عندك حصة بكرة": once a day, at ReminderHourCairo (Cairo time), tells the students about every lesson
/// the teacher put on TOMORROW's date in "مواعيد الحصص" (see StudentPushNotifier.ScheduleReminderAsync).
///
/// This is NOT a polling job: it sleeps until the next reminder time, so the database is touched once a day
/// (Neon can scale to zero in between).
///
/// CATCH-UP: shortly after the server starts it checks once. If the server was asleep or restarted after
/// the reminder time, tomorrow's reminders still go out. Duplicates are impossible (every reminder is
/// claimed in PushDispatchLogs first), so running more than once is harmless.
///
/// NOTE: it needs the server process to be running at that time. On a hosting plan that spins the server
/// down when idle (e.g. Render's free web service) the reminder goes out when the server next wakes up
/// after that hour.
/// </summary>
public class ScheduleReminderJob : BackgroundService
{
    /// <summary>Cairo hour (0-23) at which tomorrow's lessons are announced. Change it here.</summary>
    public const int ReminderHourCairo = 18;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ScheduleReminderJob> _log;

    public ScheduleReminderJob(IServiceScopeFactory scopes, ILogger<ScheduleReminderJob> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting up; this first run is also the catch-up run.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try { await Task.Delay(UntilNextRun(), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            // Too early (e.g. the server just started in the morning): the timed run will do it.
            if (PushTargets.ToCairo(DateTime.UtcNow).Hour < ReminderHourCairo) return;

            using var scope = _scopes.CreateScope();
            var push = scope.ServiceProvider.GetRequiredService<IPushService>();
            if (!push.Enabled) return; // FCM is off: nothing to send, and no database query.

            var notifier = scope.ServiceProvider.GetRequiredService<StudentPushNotifier>();
            await notifier.SendScheduleRemindersForTomorrowAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "ScheduleReminderJob run failed.");
        }
    }

    /// <summary>Time until the next ReminderHourCairo:01 (the extra minute guarantees the hour check passes).</summary>
    private static TimeSpan UntilNextRun()
    {
        var now = PushTargets.ToCairo(DateTime.UtcNow);
        var next = now.Date.AddHours(ReminderHourCairo);
        if (next <= now) next = next.AddDays(1);
        return next - now + TimeSpan.FromMinutes(1);
    }
}
