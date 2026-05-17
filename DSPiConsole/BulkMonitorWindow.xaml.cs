using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DSPiConsole.Usb;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace DSPiConsole;

/// <summary>
/// Live view of every packet read on the bulk notification endpoint (EP3).
/// Shows IDLE keep-alives, decoded v2 PARAM_CHANGED / BULK_INVALIDATED /
/// PRESET_LOADED events, unknown event IDs, and malformed packets. Useful for
/// debugging firmware notifications, verifying GPIO-driven param changes,
/// and confirming origin tags on multi-host setups.
/// </summary>
public sealed partial class BulkMonitorWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // Bounded ring buffer of decoded lines. 1000 keeps memory predictable and
    // TextBlock update cost reasonable (~80 KB max text).
    private const int MaxLines = 1000;
    private readonly Queue<string> _lines = new(MaxLines + 1);

    private readonly DspDevice _device;
    private bool _paused;
    private long _totalPackets;
    private long _droppedIdleCount;

    public BulkMonitorWindow(DspDevice device)
    {
        InitializeComponent();
        _device = device;

        // Title bar styling — matches StatsWindow / other secondary windows.
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;
        appWindow?.Resize(new Windows.Graphics.SizeInt32((int)(900 * dpiScale), (int)(600 * dpiScale)));
        if (appWindow != null)
        {
            appWindow.Title = "Bulk Endpoint Monitor";
            if (appWindow.TitleBar is { } titleBar)
            {
                titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
                titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
                titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
                titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
                titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 50, 50, 50);
            }
        }

        PauseButton.Click += OnPauseClick;
        ClearButton.Click += OnClearClick;
        UpdateStatusBar();

        // Subscribe to raw packets. Fires on the notify thread; we marshal to UI.
        _device.NotifyPacketReceived += OnPacketReceived;
        Closed += (_, _) =>
        {
            _device.NotifyPacketReceived -= OnPacketReceived;
        };
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "Resume" : "Pause";
        UpdateStatusBar();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _lines.Clear();
        _totalPackets = 0;
        _droppedIdleCount = 0;
        LogText.Text = "";
        UpdateStatusBar();
    }

    private void OnPacketReceived(object? sender, NotifyPacket pkt)
    {
        // Notification thread → marshal everything UI-touching onto the
        // dispatcher. The decoder runs there too — it's cheap (~tens of µs
        // for ≤16-byte payloads) and reading the CheckBox toggles off-thread
        // is unsafe in WinUI 3.
        bool isIdle = pkt.Data.Length < 4;
        DispatcherQueue.TryEnqueue(() =>
        {
            _totalPackets++;
            if (isIdle && ShowIdleCheck.IsChecked != true)
            {
                _droppedIdleCount++;
                UpdateStatusBar();
                return;
            }
            if (_paused)
            {
                UpdateStatusBar();
                return;
            }
            var line = NotifyPacketDecoder.Format(pkt, ShowHexCheck.IsChecked == true);
            AppendLine(line);
            UpdateStatusBar();
            if (AutoScrollCheck.IsChecked == true)
                LogScroll.ChangeView(null, double.MaxValue, null, disableAnimation: true);
        });
    }

    private void AppendLine(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > MaxLines) _lines.Dequeue();

        // Rebuild text from the bounded queue. For ~1000 lines this is a few
        // hundred microseconds and avoids string concat in a long-lived buffer.
        var sb = new StringBuilder(_lines.Count * 80);
        foreach (var l in _lines)
        {
            sb.Append(l);
            sb.Append('\n');
        }
        // Drop trailing newline to keep the last line tight against the bottom.
        if (sb.Length > 0) sb.Length--;
        LogText.Text = sb.ToString();
    }

    private void UpdateStatusBar()
    {
        StatusText.Text = _paused ? "Paused" : "Running";
        CountText.Text = string.Format(CultureInfo.InvariantCulture,
            "{0} packets   ({1} IDLE hidden)   {2} lines shown",
            _totalPackets, _droppedIdleCount, _lines.Count);
    }
}

/// <summary>
/// Decodes a <see cref="NotifyPacket"/> into one human-readable log line.
/// Shares offset constants with <c>BulkParamsParser</c> and event IDs with
/// <c>DspDevice.ProcessNotifyPacket</c>, but emits a richer description
/// (origin tag names, offset → field decoding, optional hex dump).
/// </summary>
internal static class NotifyPacketDecoder
{
    // ── WireBulkParams section offsets ─────────────────────────────────
    // Authoritative struct layout: firmware/DSPi/bulk_params.h.
    // BulkParamsParser uses the same numbers; keep these in sync.
    private const int GlobalWireOffset       = 16;     // WireGlobalParams (16B)
    private const int CrossfeedWireOffset    = 32;     // WireCrossfeedParams (16B)
    private const int DelaysWireOffset       = 64;     // float[11] delay_ms
    private const int CrosspointsWireOffset  = 108;    // WireCrosspoint[2][9] (8B each)
    private const int OutputsWireOffset      = 252;    // WireOutputChannel[9] (12B each)
    private const int PinConfigWireOffset    = 360;    // WirePinConfig (8B)
    // 368 = OffsetEq (still inline in DescribeOffset)
    private const int ChannelNamesWireOffset = 2480;
    private const int WireChannelNameLen     = 32;
    private const int I2sConfigWireOffset    = 2832;   // WireI2SConfig (16B)
    private const int LevellerWireOffset     = 2848;   // WireLevellerConfig (16B)
    private const int PreampWireOffset       = 2864;   // WirePreampConfig (16B)
    private const int MasterVolumeWireOffset = 2880;
    private const int InputSourceWireOffset  = 2896;   // WireInputConfig (16B)
    private const int LgSoundSyncWireOffset  = 2912;   // WireLgSoundSync (16B)
    private const int UserVolumeWireOffset   = 2928;
    private const int DacHwMuteWireOffset    = 2944;   // WireDacHwMute (16B, V10+)

    // ── Field offsets within Global block (offset 16) ──
    private const int PreampGainDbWireOffset       = GlobalWireOffset + 0;   // float
    private const int BypassWireOffset             = GlobalWireOffset + 4;   // u8
    private const int LoudnessEnabledWireOffset    = GlobalWireOffset + 5;   // u8
    private const int LoudnessRefSplWireOffset     = GlobalWireOffset + 8;   // float
    private const int LoudnessIntensityWireOffset  = GlobalWireOffset + 12;  // float

    // ── Field offsets within Crossfeed block (offset 32) ──
    private const int CrossfeedEnabledWireOffset   = CrossfeedWireOffset + 0;   // u8
    private const int CrossfeedPresetWireOffset    = CrossfeedWireOffset + 1;   // u8
    private const int CrossfeedItdWireOffset       = CrossfeedWireOffset + 2;   // u8
    private const int CrossfeedFcWireOffset        = CrossfeedWireOffset + 4;   // float
    private const int CrossfeedFeedDbWireOffset    = CrossfeedWireOffset + 8;   // float

    // ── Field offsets within I²S block (offset 2832) ──
    private const int OutputSlotTypesWireOffset = I2sConfigWireOffset;          // 2832 (4×u8)
    private const int BckPinWireOffset          = I2sConfigWireOffset + 4;      // 2836
    private const int MckPinWireOffset          = I2sConfigWireOffset + 5;      // 2837
    private const int MckEnabledWireOffset      = I2sConfigWireOffset + 6;      // 2838
    private const int MckMultiplierWireOffset   = I2sConfigWireOffset + 7;      // 2839

    // ── Field offsets within Leveller block (offset 2848) ──
    private const int LevellerEnabledWireOffset    = LevellerWireOffset + 0;   // u8
    private const int LevellerSpeedWireOffset      = LevellerWireOffset + 1;   // u8 (0=Slow,1=Medium,2=Fast)
    private const int LevellerLookaheadWireOffset  = LevellerWireOffset + 2;   // u8
    private const int LevellerAmountWireOffset     = LevellerWireOffset + 4;   // float
    private const int LevellerMaxGainWireOffset    = LevellerWireOffset + 8;   // float
    private const int LevellerGateWireOffset       = LevellerWireOffset + 12;  // float

    // ── Field offsets within Preamp block (offset 2864) ──
    private const int PreampLDbWireOffset = PreampWireOffset + 0;   // float (input L)
    private const int PreampRDbWireOffset = PreampWireOffset + 4;   // float (input R)

    // ── Field offsets within Input block (offset 2896) ──
    private const int SpdifRxPinWireOffset = InputSourceWireOffset + 1;   // 2897

    // ── Wire-struct sizes (helps the dispatcher recognise whole-struct writes) ──
    private const int WireCrosspointSize    = 8;     // WireCrosspoint
    private const int WireOutputChannelSize = 12;    // WireOutputChannel
    private const int WireDacHwMuteSize     = 16;    // WireDacHwMute

    public static string Format(NotifyPacket pkt, bool includeHex)
    {
        var ts = pkt.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var data = pkt.Data;
        var sb = new StringBuilder(128);
        sb.Append('[').Append(ts).Append("] ");

        if (data.Length < 4)
        {
            // 1-byte IDLE keep-alive (or anything else too small for v2 header).
            sb.Append("IDLE  ").Append(data.Length).Append('B');
            if (includeHex) AppendHex(sb, data, data.Length);
            return sb.ToString();
        }

        byte version = data[0];
        byte eventId = data[1];
        // data[2] = flags (must be 0 in v2)
        byte seq = data[3];

        if (version == 0x01)
        {
            // Legacy v1 master-volume packet — DspDevice currently ignores it.
            sb.Append("v1  ").Append(data.Length).Append('B');
            if (includeHex) AppendHex(sb, data, data.Length);
            return sb.ToString();
        }
        if (version != 0x02)
        {
            sb.Append("UNKNOWN version=0x").Append(version.ToString("X2"));
            if (includeHex) AppendHex(sb, data, data.Length);
            return sb.ToString();
        }

        switch (eventId)
        {
            case 0x02: FormatParamChanged(sb, data, seq); break;
            case 0x03: FormatBulkInvalidated(sb, data, seq); break;
            case 0x04: FormatPresetLoaded(sb, data, seq); break;
            default:
                sb.Append("UNKNOWN event=0x").Append(eventId.ToString("X2"))
                  .Append(" seq=").Append(seq);
                break;
        }
        if (includeHex) AppendHex(sb, data, data.Length);
        return sb.ToString();
    }

    private static void FormatParamChanged(StringBuilder sb, byte[] data, byte seq)
    {
        if (data.Length < 12)
        {
            sb.Append("PARAM_CHANGED (truncated, ").Append(data.Length).Append("B)");
            return;
        }
        ushort offset = BitConverter.ToUInt16(data, 4);
        ushort size = BitConverter.ToUInt16(data, 6);
        byte source = data[8];
        sb.Append("PARAM_CHANGED  seq=").Append(seq)
          .Append(" src=").Append(SourceName(source))
          .Append(" offset=").Append(offset)
          .Append(" size=").Append(size);
        // Region decoding — same offset map ProcessNotifyPacket dispatches on.
        var region = DescribeOffset(offset, size);
        if (region != null) sb.Append(" (").Append(region).Append(')');

        // Payload summary for the common cases.
        if (12 + size <= data.Length)
            AppendPayloadSummary(sb, data, 12, size, offset);
    }

    private static void FormatBulkInvalidated(StringBuilder sb, byte[] data, byte seq)
    {
        sb.Append("BULK_INVALIDATED  seq=").Append(seq);
        if (data.Length >= 5)
            sb.Append(" src=").Append(SourceName(data[4]));
    }

    private static void FormatPresetLoaded(StringBuilder sb, byte[] data, byte seq)
    {
        sb.Append("PRESET_LOADED  seq=").Append(seq);
        if (data.Length >= 5)
            sb.Append(" slot=").Append(data[4]);
    }

    private static string? DescribeOffset(int offset, int size)
    {
        // EQ band region: 11 channels × 12 bands × 16 bytes = 2112 bytes
        const int OffsetEq = 368;
        const int WireBandSize = 16;
        const int WireMaxBands = 12;
        const int WireMaxChannels = 11;
        if (size == WireBandSize
            && offset >= OffsetEq
            && offset < OffsetEq + WireMaxChannels * WireMaxBands * WireBandSize
            && (offset - OffsetEq) % WireBandSize == 0)
        {
            int flat = (offset - OffsetEq) / WireBandSize;
            return $"eq[ch={flat / WireMaxBands},band={flat % WireMaxBands}]";
        }
        // Channel name
        if (size == WireChannelNameLen
            && offset >= ChannelNamesWireOffset
            && offset < ChannelNamesWireOffset + 11 * WireChannelNameLen
            && (offset - ChannelNamesWireOffset) % WireChannelNameLen == 0)
        {
            return $"channel_names[{(offset - ChannelNamesWireOffset) / WireChannelNameLen}]";
        }
        // Global / master controls
        if (size == 4 && offset == PreampGainDbWireOffset) return "global.preamp_gain_db";
        if (size == 1 && offset == BypassWireOffset) return "global.bypass";
        if (size == 1 && offset == LoudnessEnabledWireOffset) return "global.loudness_enabled";
        if (size == 4 && offset == LoudnessRefSplWireOffset) return "global.loudness_ref_spl";
        if (size == 4 && offset == LoudnessIntensityWireOffset) return "global.loudness_intensity_pct";

        // Crossfeed
        if (size == 1 && offset == CrossfeedEnabledWireOffset) return "crossfeed.enabled";
        if (size == 1 && offset == CrossfeedPresetWireOffset) return "crossfeed.preset";
        if (size == 1 && offset == CrossfeedItdWireOffset) return "crossfeed.itd_enabled";
        if (size == 4 && offset == CrossfeedFcWireOffset) return "crossfeed.custom_fc";
        if (size == 4 && offset == CrossfeedFeedDbWireOffset) return "crossfeed.custom_feed_db";

        // Per-channel delays — float[11] at offset 64
        if (size == 4
            && offset >= DelaysWireOffset
            && offset < DelaysWireOffset + 11 * 4
            && (offset - DelaysWireOffset) % 4 == 0)
        {
            return $"delays[{(offset - DelaysWireOffset) / 4}]";
        }

        // Matrix crosspoints — WireCrosspoint[2][9] at offset 108
        if (size == WireCrosspointSize
            && offset >= CrosspointsWireOffset
            && offset < CrosspointsWireOffset + 2 * 9 * WireCrosspointSize
            && (offset - CrosspointsWireOffset) % WireCrosspointSize == 0)
        {
            int idx = (offset - CrosspointsWireOffset) / WireCrosspointSize;
            return $"crosspoints[in={idx / 9},out={idx % 9}]";
        }

        // Per-output channel — WireOutputChannel[9] at offset 252
        // Firmware fires individual field writes, not the whole struct.
        if (offset >= OutputsWireOffset
            && offset < OutputsWireOffset + 9 * WireOutputChannelSize)
        {
            int outIdx = (offset - OutputsWireOffset) / WireOutputChannelSize;
            int fieldOff = (offset - OutputsWireOffset) % WireOutputChannelSize;
            if (size == 1 && fieldOff == 0) return $"outputs[{outIdx}].enabled";
            if (size == 1 && fieldOff == 1) return $"outputs[{outIdx}].mute";
            if (size == 4 && fieldOff == 4) return $"outputs[{outIdx}].gain_db";
            if (size == 4 && fieldOff == 8) return $"outputs[{outIdx}].delay_ms";
        }

        // Pin config — num_pin_outputs(1) + pins[5] at offset 360
        if (size == 1 && offset == PinConfigWireOffset) return "pins.num_pin_outputs";
        if (size == 1
            && offset >= PinConfigWireOffset + 1
            && offset < PinConfigWireOffset + 6)
        {
            return $"pins.pins[{offset - PinConfigWireOffset - 1}]";
        }

        // I²S configuration block
        if (size == 1
            && offset >= OutputSlotTypesWireOffset
            && offset < OutputSlotTypesWireOffset + 4)
        {
            return $"i2s_config.output_types[{offset - OutputSlotTypesWireOffset}]";
        }
        if (size == 1 && offset == BckPinWireOffset) return "i2s_config.bck_pin";
        if (size == 1 && offset == MckPinWireOffset) return "i2s_config.mck_pin";
        if (size == 1 && offset == MckEnabledWireOffset) return "i2s_config.mck_enabled";
        if (size == 1 && offset == MckMultiplierWireOffset) return "i2s_config.mck_multiplier";

        // Leveller
        if (size == 1 && offset == LevellerEnabledWireOffset) return "leveller.enabled";
        if (size == 1 && offset == LevellerSpeedWireOffset) return "leveller.speed";
        if (size == 1 && offset == LevellerLookaheadWireOffset) return "leveller.lookahead";
        if (size == 4 && offset == LevellerAmountWireOffset) return "leveller.amount";
        if (size == 4 && offset == LevellerMaxGainWireOffset) return "leveller.max_gain_db";
        if (size == 4 && offset == LevellerGateWireOffset) return "leveller.gate_threshold_db";

        // Per-channel preamp (V6+)
        if (size == 4 && offset == PreampLDbWireOffset) return "preamp.preamp_db[L]";
        if (size == 4 && offset == PreampRDbWireOffset) return "preamp.preamp_db[R]";

        // Master / input / user-volume (already-known)
        if (size == 4 && offset == MasterVolumeWireOffset) return "master_volume_db";
        if (size == 1 && offset == InputSourceWireOffset) return "input_source";
        if (size == 1 && offset == SpdifRxPinWireOffset) return "spdif_rx_pin";
        if (size == 4 && offset == UserVolumeWireOffset) return "user_volume_db";
        if (size == 1 && offset == UserVolumeWireOffset + 4) return "user_mute";

        // DAC HW Mute (V10+) — firmware fires the whole 16-byte struct
        if (size == WireDacHwMuteSize && offset == DacHwMuteWireOffset)
            return "dac_hw_mute (full struct)";

        return null;
    }

    private static void AppendPayloadSummary(StringBuilder sb, byte[] data, int payloadOff, int size, int wireOffset)
    {
        // Recognized scalar payloads — keep these tight; the hex dump option
        // covers everything else.
        if (size == 1 && wireOffset == InputSourceWireOffset)
        {
            sb.Append(" → ").Append(data[payloadOff] == 1 ? "SPDIF" : "USB");
            return;
        }
        if (size == 4 && (wireOffset == MasterVolumeWireOffset || wireOffset == UserVolumeWireOffset))
        {
            float db = BitConverter.ToSingle(data, payloadOff);
            sb.Append(" → ").Append(db.ToString("F1", CultureInfo.InvariantCulture)).Append(" dB");
            return;
        }
        // I²S configuration block
        if (size == 1
            && wireOffset >= OutputSlotTypesWireOffset
            && wireOffset < OutputSlotTypesWireOffset + 4)
        {
            sb.Append(" → ").Append(data[payloadOff] == 1 ? "I²S" : "S/PDIF");
            return;
        }
        if (size == 1 && (wireOffset == BckPinWireOffset
                       || wireOffset == MckPinWireOffset
                       || wireOffset == SpdifRxPinWireOffset))
        {
            sb.Append(" → GPIO ").Append(data[payloadOff]);
            return;
        }
        if (size == 1 && wireOffset == MckEnabledWireOffset)
        {
            sb.Append(" → ").Append(data[payloadOff] != 0 ? "enabled" : "disabled");
            return;
        }
        if (size == 1 && wireOffset == MckMultiplierWireOffset)
        {
            // Firmware encodes 0 → 128×, 1 → 256×. Anything else is
            // unexpected; show the raw byte so unknown encodings stand out.
            byte enc = data[payloadOff];
            string label = enc switch
            {
                0 => "128×",
                1 => "256×",
                _ => $"unknown (raw=0x{enc:X2})"
            };
            sb.Append(" → ").Append(label);
            return;
        }

        // ── Global / loudness ─────────────────────────────────────────
        if (size == 4 && wireOffset == PreampGainDbWireOffset)
        {
            float db = BitConverter.ToSingle(data, payloadOff);
            sb.Append(" → ").Append(db.ToString("F1", CultureInfo.InvariantCulture)).Append(" dB");
            return;
        }
        if (size == 1 && (wireOffset == BypassWireOffset || wireOffset == LoudnessEnabledWireOffset))
        {
            sb.Append(" → ").Append(data[payloadOff] != 0 ? "on" : "off");
            return;
        }
        if (size == 4 && wireOffset == LoudnessRefSplWireOffset)
        {
            sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F1", CultureInfo.InvariantCulture));
            return;
        }
        if (size == 4 && wireOffset == LoudnessIntensityWireOffset)
        {
            sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F1", CultureInfo.InvariantCulture)).Append('%');
            return;
        }

        // ── Crossfeed ─────────────────────────────────────────────────
        if (size == 1 && (wireOffset == CrossfeedEnabledWireOffset
                       || wireOffset == CrossfeedItdWireOffset))
        {
            sb.Append(" → ").Append(data[payloadOff] != 0 ? "on" : "off");
            return;
        }
        if (size == 1 && wireOffset == CrossfeedPresetWireOffset)
        {
            sb.Append(" → preset ").Append(data[payloadOff]);
            return;
        }
        if (size == 4 && wireOffset == CrossfeedFcWireOffset)
        {
            sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F1", CultureInfo.InvariantCulture)).Append(" Hz");
            return;
        }
        if (size == 4 && wireOffset == CrossfeedFeedDbWireOffset)
        {
            sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F1", CultureInfo.InvariantCulture)).Append(" dB");
            return;
        }

        // ── Per-channel delay (float ms in delays[]) ──────────────────
        if (size == 4
            && wireOffset >= DelaysWireOffset
            && wireOffset < DelaysWireOffset + 11 * 4
            && (wireOffset - DelaysWireOffset) % 4 == 0)
        {
            sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F2", CultureInfo.InvariantCulture)).Append(" ms");
            return;
        }

        // ── Matrix crosspoint (8-byte struct) ─────────────────────────
        if (size == WireCrosspointSize
            && wireOffset >= CrosspointsWireOffset
            && wireOffset < CrosspointsWireOffset + 2 * 9 * WireCrosspointSize)
        {
            bool xenabled = data[payloadOff + 0] != 0;
            bool xinvert  = data[payloadOff + 1] != 0;
            float xgain   = BitConverter.ToSingle(data, payloadOff + 4);
            sb.Append(" → ").Append(xenabled ? "on" : "off")
              .Append(xinvert ? " ϕ" : "")
              .Append(" g=").Append(xgain.ToString("F1", CultureInfo.InvariantCulture)).Append(" dB");
            return;
        }

        // ── Per-output channel fields ─────────────────────────────────
        if (wireOffset >= OutputsWireOffset
            && wireOffset < OutputsWireOffset + 9 * WireOutputChannelSize)
        {
            int fieldOff = (wireOffset - OutputsWireOffset) % WireOutputChannelSize;
            if (size == 1 && (fieldOff == 0 || fieldOff == 1))
            {
                sb.Append(" → ").Append(data[payloadOff] != 0 ? "on" : "off");
                return;
            }
            if (size == 4 && fieldOff == 4)
            {
                sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F1", CultureInfo.InvariantCulture)).Append(" dB");
                return;
            }
            if (size == 4 && fieldOff == 8)
            {
                sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F2", CultureInfo.InvariantCulture)).Append(" ms");
                return;
            }
        }

        // ── PinConfig — num + pins ────────────────────────────────────
        if (size == 1 && wireOffset == PinConfigWireOffset)
        {
            sb.Append(" → ").Append(data[payloadOff]);
            return;
        }
        if (size == 1
            && wireOffset >= PinConfigWireOffset + 1
            && wireOffset < PinConfigWireOffset + 6)
        {
            sb.Append(" → GPIO ").Append(data[payloadOff]);
            return;
        }

        // ── Leveller ──────────────────────────────────────────────────
        if (size == 1 && (wireOffset == LevellerEnabledWireOffset
                       || wireOffset == LevellerLookaheadWireOffset))
        {
            sb.Append(" → ").Append(data[payloadOff] != 0 ? "on" : "off");
            return;
        }
        if (size == 1 && wireOffset == LevellerSpeedWireOffset)
        {
            string label = data[payloadOff] switch
            {
                0 => "Slow",
                1 => "Medium",
                2 => "Fast",
                _ => $"unknown (raw={data[payloadOff]})"
            };
            sb.Append(" → ").Append(label);
            return;
        }
        if (size == 4 && wireOffset == LevellerAmountWireOffset)
        {
            sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F1", CultureInfo.InvariantCulture)).Append('%');
            return;
        }
        if (size == 4 && (wireOffset == LevellerMaxGainWireOffset
                       || wireOffset == LevellerGateWireOffset))
        {
            sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F1", CultureInfo.InvariantCulture)).Append(" dB");
            return;
        }

        // ── Per-channel preamp ────────────────────────────────────────
        if (size == 4 && (wireOffset == PreampLDbWireOffset || wireOffset == PreampRDbWireOffset))
        {
            sb.Append(" → ").Append(BitConverter.ToSingle(data, payloadOff).ToString("F1", CultureInfo.InvariantCulture)).Append(" dB");
            return;
        }

        // ── DAC HW Mute (whole 16-byte struct write) ──────────────────
        if (size == WireDacHwMuteSize && wireOffset == DacHwMuteWireOffset)
        {
            // WireDacHwMute: enabled(1) active_low(1) pin(1) reserved(1) hold_ms(u16) release_ms(u16) reserved[8]
            byte enabled    = data[payloadOff + 0];
            byte activeLow  = data[payloadOff + 1];
            byte pin        = data[payloadOff + 2];
            ushort holdMs   = BitConverter.ToUInt16(data, payloadOff + 4);
            ushort releaseMs= BitConverter.ToUInt16(data, payloadOff + 6);
            sb.Append(" → ")
              .Append(enabled != 0 ? "on" : "off")
              .Append(", pin=").Append(pin == 0xFF ? "none" : $"GPIO {pin}")
              .Append(", ").Append(activeLow != 0 ? "active-low" : "active-high")
              .Append(", hold=").Append(holdMs).Append(" ms")
              .Append(", release=").Append(releaseMs).Append(" ms");
            return;
        }
        if (size == 16)
        {
            // WireBandParams: type(1), bypass(1), reserved(2), freq(4), Q(4), gain(4)
            // Heuristic: if first byte looks like a FilterType (0..7) and the rest
            // are plausible floats, decode it. Otherwise stay quiet.
            byte type = data[payloadOff];
            if (type <= 7)
            {
                byte bypass = data[payloadOff + 1];
                float freq = BitConverter.ToSingle(data, payloadOff + 4);
                float q = BitConverter.ToSingle(data, payloadOff + 8);
                float gain = BitConverter.ToSingle(data, payloadOff + 12);
                sb.Append(" → type=").Append(type)
                  .Append(bypass == 1 ? " BYPASSED" : "")
                  .Append(" f=").Append(freq.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(" q=").Append(q.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(" g=").Append(gain.ToString("F1", CultureInfo.InvariantCulture));
            }
        }
    }

    private static string SourceName(byte src) => src switch
    {
        0 => "Unknown",
        1 => "HostSet",
        2 => "BulkSet",
        3 => "Preset",
        4 => "Factory",
        5 => "Gpio",
        6 => "Internal",
        7 => "Uac1",
        _ => $"src({src})"
    };

    private static void AppendHex(StringBuilder sb, byte[] data, int len)
    {
        sb.Append("  | ");
        int show = Math.Min(len, 32);
        for (int i = 0; i < show; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        if (len > show) sb.Append(" …");
    }
}
