using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// Shammy Island keeps itself up to date through this laptop (your call, 16 Nov): every Shammy release carries the newest
/// phone app (signature-checked when it was bundled). When the phone says which version it runs and this laptop has a
/// newer one, it offers it; the phone asks for it and it goes over the encrypted link in pieces. The phone checks the
/// size, the SHA-256, a higher version and that it's signed by Shammy Island's own key before Android installs it.
/// Nothing touches the internet, and only the paired phone, over the pinned link, ever gets it.
/// </summary>
internal sealed partial class PhoneLinkModule
{
    static string BundledApk => Path.Combine(AppContext.BaseDirectory, "Assets", "Phone", "ShammyIsland.apk");

    /// <summary>The bundled app's versionCode (written next to it when it's bundled), or 0.</summary>
    static int BundledVersion() =>
        int.TryParse(File.Exists(BundledApk + ".version") ? File.ReadAllText(BundledApk + ".version").Trim() : "", out int v) ? v : 0;

    bool _updateSent;

    /// <summary>The phone said which version it runs (once per connection): offer a newer one if this laptop has it.</summary>
    void OnPhoneVersion(int code)
    {
        _updateSent = false;
        int bundled = BundledVersion();
        if (bundled <= code || !File.Exists(BundledApk)) return;
        var apk = File.ReadAllBytes(BundledApk);
        Send(new JsonObject
        {
            ["t"] = "update", ["code"] = bundled, ["size"] = apk.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(apk)).ToLowerInvariant(),
        });
        _ctx.Info($"Offered Shammy Island {bundled} to the phone (it runs {code})");
    }

    /// <summary>The phone asked for the update: the bundled app in pieces, then the end (once per connection).</summary>
    void SendUpdate()
    {
        if (_updateSent || _live is not { } s || !File.Exists(BundledApk)) return;
        _updateSent = true;
        var apk = File.ReadAllBytes(BundledApk);
        _ = Task.Run(async () =>
        {
            try
            {
                await SendPiecesAsync(s, new MemoryStream(apk), (i, data) => new JsonObject { ["t"] = "chunk", ["i"] = i, ["data"] = data });
                await SendNowAsync(s, new JsonObject { ["t"] = "chunkEnd" });
                _ctx.Info($"Sent Shammy Island to the phone ({apk.Length / 1024} KB)");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                _ctx.Info("Sending the update stopped (the phone left)");
            }
        });
    }
}
