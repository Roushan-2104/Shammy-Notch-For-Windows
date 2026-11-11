using Shammy.Infrastructure;

namespace Shammy.Modules.OnlineVoice;

/// <summary>Where a held talk key's recording goes, in words for the Voice settings page and the listening view.</summary>
internal static class VoiceRoute
{
    public static bool Online(AppSettings s, bool hasKey) => hasKey && s.OnlineVoiceListens;

    public static string Describe(AppSettings s, bool hasKey) =>
        Online(s, hasKey) ? "Online through Gnani, in Hinglish and English. If Gnani is slow or offline, this PC listens instead."
        : s.VoiceMode == AppSettings.HinglishVoice ? "On this PC, in Hinglish (plain English works too). Nothing is sent anywhere."
        : "On this PC, in English. Hinglish may be misheard: pick Hinglish below, or add a Gnani key.";

    /// <summary>The small word after “Listening…”: where it is heard.</summary>
    public static string Tag(AppSettings s, bool online) =>
        online ? "online" : s.VoiceMode == AppSettings.HinglishVoice ? "Hinglish" : "English";
}
