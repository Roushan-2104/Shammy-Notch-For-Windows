using System.Text.Json.Nodes;
using Shammy.Infrastructure;
using Windows.Devices.Bluetooth.Advertisement;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// The Bluetooth beacon for the phone's one-time "companion device" step (docs/LINK.md §8): Android lets Shammy Island
/// hide other apps' pop-up banners only once it is associated with a nearby device, and its association dialog finds
/// devices over Bluetooth. So when the paired phone asks, this laptop advertises one fixed marker for 90 seconds, and
/// nothing else: no name, no connection accepted.
/// </summary>
internal sealed partial class PhoneLinkModule
{
    /// <summary>
    /// Manufacturer data under 0xFFFF (Bluetooth's id for tests and private use) holding "SHAMMY"; the phone's
    /// association filter looks for exactly this. (Windows won't advertise a service id from an app.)
    /// </summary>
    const ushort BeaconCompany = 0xFFFF;
    static readonly byte[] BeaconData = "SHAMMY"u8.ToArray();
    static readonly TimeSpan BeaconFor = TimeSpan.FromSeconds(90);

    BluetoothLEAdvertisementPublisher? _beacon;

    void Beacon()
    {
        try
        {
            StopBeacon();
            var p = new BluetoothLEAdvertisementPublisher();
            var w = new Windows.Storage.Streams.DataWriter();
            w.WriteBytes(BeaconData);
            p.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(BeaconCompany, w.DetachBuffer()));
            p.Start();
            _beacon = p;
            UiDelay.After(BeaconFor, () => { if (ReferenceEquals(_beacon, p)) StopBeacon(); }, _ctx.Ui);
            _ctx.Info($"Bluetooth beacon on for the phone's companion step ({p.Status})");
        }
        catch (Exception ex)
        {
            // Bluetooth off, or an adapter that can't advertise: the phone says so
            _ctx.Warn("The Bluetooth beacon couldn't start", ex);
            Send(new JsonObject { ["t"] = "beaconFailed" });
        }
    }

    void StopBeacon()
    {
        if (_beacon is not { } p) return;
        _beacon = null;
        try { p.Stop(); }
        catch (Exception ex) { _ctx.Warn("Stopping the Bluetooth beacon failed", ex); }
    }
}
