using Shammy.Infrastructure;

namespace Shammy.Modules.OnlineVoice;

/// <summary>Where a held talk key's recording goes, in words for the Voice settings page and the listening view.</summary>
internal static class VoiceRoute
{
    public static bool Online(AppSettings s, bool hasKey) => hasKey && s.OnlineVoiceListens;

    public static string Describe(AppSettings s, bool hasKey) =>
        Online(s, hasKey)
            ? "Online through Gnani, in Hinglish and English. Without internet, a small English backup on this PC listens instead."
            : "A small English backup on this PC, loaded only while you talk. Add a Gnani key below for Hinglish and better accuracy.";

    /// <summary>The small word after “Listening…”: where it is heard.</summary>
    public static string Tag(AppSettings s, bool online) => online ? "online" : "on this PC";
}
