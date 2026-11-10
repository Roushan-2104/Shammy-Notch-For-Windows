using System.Security.Cryptography;
using Shammy.Core.OnlineVoice;

namespace Shammy.Modules.OnlineVoice;

/// <summary>
/// The speech already paid for, on disk under the app's data folder (onlinevoice-cache): one file per utterance,
/// named by <see cref="SpeechCache.Key"/> and encrypted with Windows DPAPI for this account, because a cached
/// reminder is the reminder's words. Reading a file marks it as just used; after a write the least recently used
/// are deleted until the folder is back under 20 MB. Every failure is a log line and a cache miss.
/// </summary>
internal sealed class SpeechCacheStore(string folder, Action<string, Exception?> warn)
{
    const string Extension = ".spk";
    static readonly byte[] Entropy = "Shammy online voice cache v1"u8.ToArray();

    readonly object _gate = new();

    public byte[]? Read(string key)
    {
        var path = PathOf(key);
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var wav = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return wav;
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
            {
                // Copied from another PC or account, or half-written: drop it and fetch again.
                warn("A cached utterance was unreadable", ex);
                TryDelete(path);
                return null;
            }
        }
    }

    public void Write(string key, byte[] wav)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = PathOf(key);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, ProtectedData.Protect(wav, Entropy, DataProtectionScope.CurrentUser));
                File.Move(tmp, path, true);
                foreach (var name in SpeechCache.Evict(Entries(), SpeechCache.MaxBytes, keep: key)) TryDelete(PathOf(name));
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
            {
                warn("Caching an utterance failed", ex);
            }
        }
    }

    /// <summary>How many utterances are cached and how many bytes they take.</summary>
    public (int Count, long Bytes) Size()
    {
        lock (_gate)
        {
            try
            {
                var entries = Entries();
                return (entries.Count, entries.Sum(e => e.Bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn("Measuring the speech cache failed", ex);
                return (0, 0);
            }
        }
    }

    /// <summary>Deletes everything cached (the key was removed); returns how many files went.</summary>
    public int Clear()
    {
        lock (_gate)
        {
            int gone = 0;
            try
            {
                foreach (var e in Entries())
                    if (TryDelete(PathOf(e.Name))) gone++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn("Clearing the speech cache failed", ex);
            }
            return gone;
        }
    }

    List<CacheEntry> Entries()
    {
        var list = new List<CacheEntry>();
        if (!Directory.Exists(folder)) return list;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*" + Extension))
            list.Add(new CacheEntry(Path.GetFileNameWithoutExtension(file.Name), file.Length, file.LastWriteTimeUtc));
        return list;
    }

    string PathOf(string key) => Path.Combine(folder, key + Extension);

    bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn("Deleting a cached utterance failed", ex);
            return false;
        }
    }
}
