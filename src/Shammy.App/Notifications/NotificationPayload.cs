namespace Shammy.Notifications;

/// <summary>Activity payload for a notification on display; <see cref="More"/> counts others that arrived meanwhile.</summary>
/// <param name="Peek">A quiet peek (during a call, a meeting or focus): one silent line on the island, not the card.</param>
internal sealed record NotificationPayload(ShammyNotification Notification, int More, bool Peek = false);
