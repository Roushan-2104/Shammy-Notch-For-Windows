using System.Diagnostics;
using System.Globalization;

namespace Shammy.Search;

/// <summary>
/// One line for the state pipes ("voice-state", "captions-state"), to measure what Shammy holds:
/// "memory=ws:212MB,private:180MB,voice_model:no,webview:no".
/// </summary>
internal static class MemoryState
{
    public static string Line()
    {
        long ws = 0, priv = 0;
        try
        {
            using var me = Process.GetCurrentProcess();
            ws = me.WorkingSet64;
            priv = me.PrivateMemorySize64;
        }
        catch (Exception)
        {
        }
        bool voice = Modules.HostLinks.Voice?.IsLoaded == true;
        return string.Create(CultureInfo.InvariantCulture,
            $"memory=ws:{ws >> 20}MB,private:{priv >> 20}MB,voice_model:{(voice ? "loaded" : "no")},webview:{Views.ResultView.BrowserState}");
    }
}
