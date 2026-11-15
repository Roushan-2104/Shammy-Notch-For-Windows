using System.Text.Json.Nodes;
using Shammy.Notifications;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// One list, both devices (your call, 16 Nov): dismissing a notification on either device removes it on both. You
/// dismissing a phone notification here dismisses it on the phone; the phone dismissing one of this laptop's
/// notifications takes it out of Shammy's list and Windows' notification centre. Each side can only dismiss what it
/// was sent: this laptop remembers which of its notifications went to the phone.
/// </summary>
internal sealed partial class PhoneLinkModule
{
    const int MaxSentToasts = 200;

    /// <summary>This laptop's notifications that went to the phone, oldest first (only these can be dismissed from there).</summary>
    readonly LinkedList<uint> _sentToasts = new();

    void StartSync()
    {
        _ctx.Services.Notifications.Dismissed += OnDismissedHere;
        // test hook: dismiss the newest phone notification in the list, as its × would
        _ctx.AddPipeCommand("phonelink-dismiss-newest", _ =>
            _shown.Values.OrderByDescending(n => n.Time).FirstOrDefault() is { } n ? Done(n) : "error: nothing from the phone");
        string Done(ShammyNotification n) { _ctx.Services.Notifications.Dismiss(n); return "dismissed"; }
    }

    void StopSync() => _ctx.Services.Notifications.Dismissed -= OnDismissedHere;

    void RememberSentToast(uint id)
    {
        _sentToasts.AddLast(id);
        if (_sentToasts.Count > MaxSentToasts) _sentToasts.RemoveFirst();
    }

    /// <summary>You dismissed notifications here (×, Dismiss or Clear all): the phone drops the same ones.</summary>
    void OnDismissedHere(IReadOnlyList<ShammyNotification> gone)
    {
        foreach (var n in gone)
        {
            var key = _shown.FirstOrDefault(kv => ReferenceEquals(kv.Value, n)).Key;
            if (key is not null)
            {
                _shown.Remove(key);
                if (_replyKey == key) _replyKey = null;
                Send(new JsonObject { ["t"] = "dismiss", ["key"] = key });
            }
            else if (_sentToasts.Remove(n.Id)) Send(new JsonObject { ["t"] = "momentGone", ["id"] = $"toast.{n.Id}" });
        }
    }

    /// <summary>The phone dismissed one of this laptop's notifications (only one it was sent): gone here, and from Windows.</summary>
    void DismissFromPhone(string id)
    {
        if (!id.StartsWith("toast.", StringComparison.Ordinal) || !uint.TryParse(id.AsSpan(6), out uint toast) || !_sentToasts.Remove(toast)) return;
        var history = _ctx.Services.Notifications.History;
        if (history.FirstOrDefault(x => x.Id == toast) is { } n) history.Remove(n);
        _ctx.Services.Notifications.RemoveFromWindows(toast);
    }
}
