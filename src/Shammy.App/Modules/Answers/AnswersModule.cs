using System.Globalization;
using Shammy.Core.Notifications;
using Shammy.Core.Productivity;
using Shammy.Core.Search;
using Shammy.Infrastructure;
using Shammy.Modules.Guide;

namespace Shammy.Modules.Answers;

/// <summary>
/// Simple questions answered on the PC, never searched: the battery, the time and date, new notifications and the
/// last one, what's playing, and the way into Shammy's own settings and what's new. Any wording
/// <see cref="SpokenRewrite"/> knows works, in English or Hinglish ("battery kitni hai", "kitne baje hain").
/// A message's words stay hidden while the screen is shared or recorded.
/// </summary>
internal sealed class AnswersModule : IShammyModule
{
    ModuleContext _ctx = null!;

    public string Name => "answers";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        ctx.AddCommand(new Command(this));
        ctx.AddPipeCommand("answer", text => LocalQuestion.Parse(text) is { } kind ? Describe(Answer(kind, run: false)) : "none");
    }

    public void Dispose() { }

    static string Describe(CommandResult r) => $"{r.Title} | {r.Detail}";

    CommandResult Answer(LocalQuestionKind kind, bool run)
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        switch (kind)
        {
            case LocalQuestionKind.Battery:
            {
                var b = _ctx.Services.Battery;
                if (!b.HasBattery) return new CommandResult("", "No battery", "This PC runs on mains power");
                return new CommandResult(b.IsCharging ? "" : "", $"{b.Percent}%",
                    LocalQuestion.Battery(b.Percent, b.IsPluggedIn, null).Split(" · ", 2)[^1]);
            }
            case LocalQuestionKind.Time:
                return new CommandResult("", now.ToString(ClockPreference.Is24Hour ? "HH:mm" : "h:mm tt", culture),
                    now.ToString("dddd, d MMMM", culture));
            case LocalQuestionKind.Date:
                return new CommandResult("", now.ToString("dddd, d MMMM", culture), now.ToString("yyyy", culture) + " · week " +
                    ISOWeek.GetWeekOfYear(now).ToString(CultureInfo.InvariantCulture));
            case LocalQuestionKind.Notifications:
            {
                var n = _ctx.Services.Notifications;
                int unread = n.UnreadCount;
                if (n.History.Count == 0) return new CommandResult("", "No notifications", "Nothing has come in since Shammy started");
                var apps = n.History.Take(Math.Max(unread, 1)).Select(x => x.AppName).Distinct().Take(3);
                return new CommandResult("", unread == 0 ? "Nothing new" : unread == 1 ? "1 new notification" : $"{unread} new notifications",
                    unread == 0 ? "Latest from " + string.Join(", ", apps) : string.Join(", ", apps));
            }
            case LocalQuestionKind.LastMessage:
            {
                var last = _ctx.Services.Notifications.History.OrderByDescending(x => x.Time).FirstOrDefault();
                if (last is null) return new CommandResult("", "No messages yet", "Nothing has come in since Shammy started");
                var (title, body) = NotificationText.ForPopup(last.AppName, last.Title, last.Body, PrivacyState.Presenting);
                if (run && !PrivacyState.Presenting) HostLinks.Say?.Invoke($"{last.Title}. {last.Body}");
                return new CommandResult("", title, $"{body} · {last.AppName}, {Ago(now - last.Time.LocalDateTime)}");
            }
            case LocalQuestionKind.ShammySettings:
                if (run) _ctx.OpenCard(Settings.SettingsModule.ModuleName);
                return new CommandResult("", "Shammy settings", "Opened in the notch");
            case LocalQuestionKind.WhatsNew:
                if (run) GuideLinks.OpenNew?.Invoke();
                return new CommandResult("", "What's new", "In the guide");
            case LocalQuestionKind.NowPlaying:
            {
                var m = _ctx.Services.Media;
                if (!m.HasSession || m.Title.Length == 0) return new CommandResult("", "Nothing playing", "Start a song and ask again");
                return new CommandResult("", m.Title, m.Artist.Length > 0 ? m.Artist : "Now playing");
            }
            default:
                return new CommandResult("", "I don't know that one", IsError: true);
        }
    }

    static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "just now" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago" : t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago" : $"{(int)t.TotalDays} d ago";

    sealed class Command(AnswersModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => LocalQuestion.Parse(text) is not null;

        public Task<CommandResult> RunAsync(string text) =>
            Task.FromResult(LocalQuestion.Parse(text) is { } kind ? module.Answer(kind, run: true) : new CommandResult("", "Not a question I answer", IsError: true));

        public CommandPreview? Preview(string text) => LocalQuestion.Parse(text) is { } kind
            ? new CommandPreview("", kind switch
            {
                LocalQuestionKind.Battery => "Battery",
                LocalQuestionKind.Time => "The time",
                LocalQuestionKind.Date => "Today's date",
                LocalQuestionKind.Notifications => "New notifications",
                LocalQuestionKind.LastMessage => "Your last message",
                LocalQuestionKind.ShammySettings => "Shammy settings",
                LocalQuestionKind.WhatsNew => "What's new",
                _ => "What's playing",
            }, "Answered on this PC")
            : null;
    }
}
