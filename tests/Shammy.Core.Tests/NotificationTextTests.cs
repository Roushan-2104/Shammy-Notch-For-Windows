using Shammy.Core.Notifications;

namespace Shammy.Core.Tests;

public class NotificationTextTests
{
    [Fact]
    public void List_rows_show_title_app_and_body()
    {
        Assert.Equal(("Riya", "WhatsApp · see you at 6"), NotificationText.ForList("WhatsApp", "Riya", "see you at 6", presenting: false));
    }

    [Fact]
    public void List_rows_keep_the_sender_and_hide_the_message_while_private()
    {
        var (title, detail) = NotificationText.ForList("WhatsApp", "Riya", "the door code is 4471", presenting: true);
        Assert.Equal("Riya", title);
        Assert.Equal($"WhatsApp · {NotificationText.ListHidden}", detail);
        Assert.DoesNotContain("4471", title + detail);
    }

    [Fact]
    public void List_rows_with_no_body_hide_the_title_too_while_private()
    {
        var (title, detail) = NotificationText.ForList("Bank", "Your OTP is 4471", "", presenting: true);
        Assert.Equal("Bank", title);
        Assert.DoesNotContain("4471", title + detail);
    }

    [Fact]
    public void List_rows_hide_the_message_with_the_always_hide_setting()
    {
        Assert.Equal(("Riya", $"WhatsApp · {NotificationText.PopupAlwaysHidden}"), NotificationText.ForList("WhatsApp", "Riya", "the door code is 4471", false, alwaysHide: true));
        Assert.Equal(("Bank", NotificationText.PopupAlwaysHidden), NotificationText.ForList("Bank", "Your OTP is 4471", "", false, alwaysHide: true));
    }

    [Fact]
    public void List_rows_flatten_multi_line_bodies_and_skip_empty_ones()
    {
        Assert.Equal(("Mail", "Outlook · line one line two"), NotificationText.ForList("Outlook", "Mail", "line one\n\n line two ", false));
        Assert.Equal(("Done", "Downloads"), NotificationText.ForList("Downloads", "Done", "", false));
    }

    [Fact]
    public void Popups_keep_their_text_unless_private()
    {
        Assert.Equal(("Riya", "hi"), NotificationText.ForPopup("WhatsApp", "Riya", "hi", false));
        Assert.Equal(("Riya", NotificationText.PopupHidden), NotificationText.ForPopup("WhatsApp", "Riya", "hi", true));
    }

    [Fact]
    public void A_popup_with_no_body_may_carry_its_message_in_the_title_so_that_hides_too()
    {
        Assert.Equal(("Bank", NotificationText.PopupHidden), NotificationText.ForPopup("Bank", "Your OTP is 4471", "  ", true));
        Assert.Equal(("Bank", NotificationText.PopupAlwaysHidden), NotificationText.ForPopup("Bank", "Your OTP is 4471", "", false, alwaysHide: true));
    }

    [Fact]
    public void The_always_hide_setting_does_not_claim_you_are_sharing()
    {
        Assert.Equal(("Riya", NotificationText.PopupAlwaysHidden), NotificationText.ForPopup("WhatsApp", "Riya", "hi", false, alwaysHide: true));
        Assert.Equal(("Riya", NotificationText.PopupHidden), NotificationText.ForPopup("WhatsApp", "Riya", "hi", true, alwaysHide: true));
    }

    [Fact]
    public void Hiding_twice_changes_nothing()
    {
        var once = NotificationText.ForPopup("WhatsApp", "Riya", "hi", true);
        Assert.Equal(once, NotificationText.ForPopup("WhatsApp", once.Title, once.Body, true));
    }
}
