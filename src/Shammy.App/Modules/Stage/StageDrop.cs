using System.Runtime.InteropServices;
using System.Windows;
using Shammy.Core.Prompter;

namespace Shammy.Modules.Stage;

/// <summary>
/// What a drag onto the island carries for Stage: one .txt or .md file, or dragged text (not a lone link). Several
/// files, pictures and anything else are left for the shelf.
/// </summary>
internal static class StageDrop
{
    /// <summary>The single script file in the drag, or null.</summary>
    public static string? File(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
                ? StageScripts.ScriptFile(files)
                : null;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException)
        {
            // Some drag sources fail to hand their data over; the drop then isn't Stage's.
            return null;
        }
    }

    /// <summary>The dragged text when it is worth reading, or null (files are never read as text).</summary>
    public static string? Text(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop)) return null;
            var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string
                : data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string
                : null;
            return StageScripts.IsDroppableText(text) ? text : null;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool Accepts(IDataObject data) => File(data) is not null || Text(data) is not null;
}
