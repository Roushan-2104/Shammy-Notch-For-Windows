using System.Runtime.InteropServices;
using Shammy.Infrastructure;

namespace Shammy.Modules.Hover;

/// <summary>
/// Whether a word is English, by Windows' own spelling checker (the one Edge and Word use), on this PC. Created when a
/// hover needs it and released when the hover ends. Says null when Windows has no English dictionary.
/// </summary>
internal sealed class EnglishCheck : IDisposable
{
    static readonly string[] Tags = { "en-US", "en-GB", "en-IN", "en-AU", "en-CA" };

    ISpellChecker? _checker;
    bool _tried;

    /// <summary>True when Windows knows the word, false when it flags it, null when there's no checker.</summary>
    public bool? IsEnglish(string word)
    {
        var checker = Checker();
        if (checker is null || string.IsNullOrWhiteSpace(word)) return null;
        IEnumSpellingError? errors = null;
        try
        {
            if (checker.Check(word, out errors) != 0 || errors is null) return null;
            // One error for the word means it isn't in the dictionary.
            int hr = errors.Next(out var error);
            if (error != IntPtr.Zero) Marshal.Release(error);
            return hr != 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (errors is not null) Marshal.ReleaseComObject(errors);
        }
    }

    ISpellChecker? Checker()
    {
        if (_tried) return _checker;
        _tried = true;
        object? factoryObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"));
            if (type is null) return null;
            factoryObject = Activator.CreateInstance(type);
            if (factoryObject is not ISpellCheckerFactory factory) return null;
            foreach (var tag in Tags)
            {
                if (factory.IsSupported(tag, out var supported) != 0 || supported == 0) continue;
                if (factory.CreateSpellChecker(tag, out var checker) == 0 && checker is not null)
                {
                    _checker = checker;
                    break;
                }
            }
            if (_checker is null) Log.Info("Hover: no English spelling dictionary on this PC");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
        {
            Log.Warn("Hover: the spelling checker is unavailable", ex);
        }
        finally
        {
            if (factoryObject is not null && Marshal.IsComObject(factoryObject)) Marshal.ReleaseComObject(factoryObject);
        }
        return _checker;
    }

    public void Dispose()
    {
        if (_checker is not null)
        {
            try { Marshal.ReleaseComObject(_checker); }
            catch (Exception) { }
        }
        _checker = null;
    }

    // ---- Windows Spell Checking API (spellcheck.h), only the calls used -----------------------------------------

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellCheckerFactory
    {
        [PreserveSig] int get_SupportedLanguages(out IntPtr value);
        [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int value);
        [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker value);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellChecker
    {
        [PreserveSig] int get_LanguageTag(out IntPtr value);
        [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError value);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSpellingError
    {
        /// <summary>S_OK (0) with an error, S_FALSE (1) when there are no more.</summary>
        [PreserveSig] int Next(out IntPtr value);
    }
}
