using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Shammy.Core.Notch;

namespace Shammy.Modules.MeetingSystem;

/// <summary>
/// An app is capturing the screen (sharing it in a call, or recording it). The pet puts on its ninja mask with a
/// pulsing red dot and the running time: notification text is hidden meanwhile (PrivacyState). Windows gives no
/// way to stop another app's capture, so the circle brings that app forward instead, where its own Stop is.
/// </summary>
internal sealed class ScreenContent : ICompactContent, IPetActivity
{
    readonly DateTime _since;
    readonly string? _exePath;

    /// <param name="sharing">A call app is capturing (screen sharing) rather than a recorder.</param>
    public ScreenContent(string appName, bool sharing, DateTime since, string? exePath)
    {
        AppName = appName;
        Sharing = sharing;
        _since = since;
        _exePath = exePath;
    }

    public string AppName { get; }
    public bool Sharing { get; }

    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Recording,
        Label = Sharing ? "Sharing" : "Rec",
        Value = PetActivities.Clock(utcNow - _since),
        CanOpen = _exePath is not null,
    };

    public void RunCircle(CircleAction action)
    {
        if (action == CircleAction.Open) BringForward(_exePath);
    }

    /// <summary>Shows the capturing app's window (restoring it if minimised).</summary>
    static void BringForward(string? exePath)
    {
        if (exePath is null) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(exePath);
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                IntPtr hwnd;
                try { hwnd = p.MainWindowHandle; }
                catch (Exception) { continue; }
                if (hwnd == IntPtr.Zero) continue;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                Interop.Native.ForceForeground(hwnd);
                return;
            }
        }
    }

    const int SW_RESTORE = 9;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // The plain pill, for anywhere the pet doesn't draw it.
    public double Build(ContentControl left, ContentControl right)
    {
        left.Content = new Ellipse { Width = 8, Height = 8, Fill = Ui.Tokens.Res("Red"), VerticalAlignment = VerticalAlignment.Center };
        right.Content = new TextBlock
        {
            Text = Sharing ? $"Sharing · {AppName}" : $"Recording · {AppName}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Tokens.Res("Fg"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140,
        };
        return 140;
    }

    public UIElement? BuildBubble() => new Ellipse { Width = 9, Height = 9, Fill = Ui.Tokens.Res("Red") };
}
