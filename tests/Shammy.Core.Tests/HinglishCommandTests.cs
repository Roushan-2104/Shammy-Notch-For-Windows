using Shammy.Core.Productivity;
using Shammy.Core.Search;

namespace Shammy.Core.Tests;

/// <summary>Hinglish commands (typed, or said and spelt out) read as the English the command parser understands; all in one place.</summary>
public class HinglishCommandTests
{
    static readonly DateTime Now = new(2026, 3, 10, 15, 0, 0);

    [Theory]
    [InlineData("paanch minute ka timer laga do", "timer 5 minutes")]
    [InlineData("5 minute ka timer lagadoo.", "timer 5 minutes")]
    [InlineData("5 Minute Ka Timer Lagado", "timer 5 minutes")]
    [InlineData("das minat ka taimar set karo", "timer 10 minutes")]
    [InlineData("dedh ghante ka timer laga do", "timer 90 minutes")]
    [InlineData("ek ghante ka timer lagao", "timer 1 hour")]
    [InlineData("timer laga do 30 second ka", "timer 30 seconds")]
    [InlineData("पांच मिनट का टाइमर लगा दो", "timer 5 minutes")]
    public void Timers(string hinglish, string english) => Assert.Equal(english, HinglishCommands.ToEnglish(hinglish));

    [Theory]
    [InlineData("kal subah 7 baje yaad dilana ki doctor ko call karna", "remind me tomorrow morning at 7 to doctor ko call karna")]
    [InlineData("mujhe 10 minute baad yaad dila dena chai banana hai", "remind me in 10 minutes to chai banana hai")]
    [InlineData("aaj shaam 6 baje mummy ko phone karna yaad dilana", "remind me today evening at 6 to mummy ko phone karna")]
    [InlineData("aaj raat saade 9 baje remind karna", "remind me tonight at 9:30")]
    [InlineData("Do gante bad yad dilna ki khana khalo.", "remind me in 2 hours to khana khalo")]
    [InlineData("parso subah 9 baje yaad dilana", "remind me day after tomorrow morning at 9")]
    [InlineData("parso shaam 6 baje yaad dilana ki bill bharna", "remind me day after tomorrow evening at 6 to bill bharna")]
    public void Reminders(string hinglish, string english) => Assert.Equal(english, HinglishCommands.ToEnglish(hinglish));

    [Theory]
    [InlineData("kal subah 7 baje ka alarm laga do", "alarm tomorrow morning at 7")]
    [InlineData("kal subah saat baajay ka alarm laga do.", "alarm tomorrow morning at 7")]
    [InlineData("kal subah sath bajeka alarm laga do.", "alarm tomorrow morning at 7")]
    [InlineData("Kal Subah Saat Bajay Ka Alam Lagado.", "alarm tomorrow morning at 7")]
    [InlineData("aaj raat 10 baje alarm set karo", "alarm tonight at 10")]
    [InlineData("कल सुबह 7 बजे का अलार्म लगा दो", "alarm tomorrow morning at 7")]
    [InlineData("roz subah 7 baje ka alarm laga do", "alarm morning at 7 every day")]
    [InlineData("har din 6 baje alarm laga do", "alarm at 6 every day")]
    [InlineData("parso subah 7 baje ka alarm laga do", "alarm day after tomorrow morning at 7")]
    public void Alarms(string hinglish, string english) => Assert.Equal(english, HinglishCommands.ToEnglish(hinglish));

    [Theory]
    [InlineData("set an alarm for 7 am", 2026, 3, 11, 7, 0)]
    [InlineData("alarm at 7 tomorrow", 2026, 3, 11, 7, 0)]
    [InlineData("Wake me up at 6:30 am.", 2026, 3, 11, 6, 30)]
    [InlineData("set alarm for 5 pm", 2026, 3, 10, 17, 0)]
    [InlineData("alarm in 20 minutes", 2026, 3, 10, 15, 20)]
    [InlineData("kal subah 7 baje ka alarm laga do", 2026, 3, 11, 7, 0)]
    [InlineData("Kal Subah Saat Bajay Ka Alam Lagado.", 2026, 3, 11, 7, 0)]
    public void Alarms_are_one_off_alarms(string text, int y, int mo, int d, int h, int mi)
    {
        var command = ProductivityParser.Parse(text, Now);
        Assert.NotNull(command);
        Assert.Equal(ProductivityAction.SetAlarm, command!.Action);
        Assert.Equal(AlarmDays.None, command.Alarm!.Days);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), command.Due);
        Assert.Contains("Alarm", Shammy.Core.Palette.PaletteQuery.DescribeProductivity(command, Now));
    }

    [Theory]
    [InlineData("alarm clock app")]
    [InlineData("alarm sounds")]
    [InlineData("best alarm clock")]
    [InlineData("alarm")]
    public void Alarm_searches_stay_searches(string text) => Assert.Null(ProductivityParser.Parse(text, Now));

    [Fact]
    public void Set_an_alarm_without_a_time_asks_for_one() =>
        Assert.Equal(ProductivityAction.ReminderNeedsTime, ProductivityParser.Parse("set an alarm", Now)!.Action);

    [Fact]
    public void Typed_hinglish_parses_directly_so_the_palette_can_preview_it()
    {
        Assert.Equal(ProductivityAction.StartTimer, ProductivityParser.Parse("paanch minute ka timer laga do", Now)!.Action);
        Assert.Equal(ProductivityAction.Remind, ProductivityParser.Parse("kal subah 7 baje yaad dilana ki doctor ko call karna", Now)!.Action);
    }

    [Theory]
    [InlineData("mujhe 50000 ke under ek achha laptop batao")]
    [InlineData("alarm laga do gaana")]
    [InlineData("Set a timer for 10 minutes.")]
    [InlineData("aaj mausam kaisa hai")]
    [InlineData("")]
    public void Everything_else_is_left_to_search(string text) => Assert.Null(HinglishCommands.ToEnglish(text));

    [Theory]
    [InlineData("paanch minute ka timer laga do", ProductivityAction.StartTimer)]
    [InlineData("kal subah 7 baje yaad dilana ki doctor ko call karna", ProductivityAction.Remind)]
    [InlineData("mujhe 10 minute baad yaad dila dena chai banana hai", ProductivityAction.Remind)]
    [InlineData("aaj shaam 6 baje mummy ko phone karna yaad dilana", ProductivityAction.Remind)]
    public void The_english_is_what_the_command_parser_understands(string hinglish, ProductivityAction action)
    {
        var english = HinglishCommands.ToEnglish(hinglish);
        var command = ProductivityParser.Parse(english!, Now);
        Assert.NotNull(command);
        Assert.Equal(action, command!.Action);
    }

    [Fact]
    public void Reminder_times_resolve_as_meant()
    {
        var command = ProductivityParser.Parse(HinglishCommands.ToEnglish("kal subah 7 baje yaad dilana ki doctor ko call karna")!, Now)!;
        Assert.Equal(new DateTime(2026, 3, 11, 7, 0, 0), command.Due);
        Assert.Equal("Doctor ko call karna", command.Text);
        var evening = ProductivityParser.Parse(HinglishCommands.ToEnglish("aaj shaam 6 baje mummy ko phone karna yaad dilana")!, Now)!;
        Assert.Equal(new DateTime(2026, 3, 10, 18, 0, 0), evening.Due);
    }
}
