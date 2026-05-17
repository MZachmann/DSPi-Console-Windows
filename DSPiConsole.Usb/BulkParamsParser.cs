using System.Text;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Usb;

/// <summary>
/// Parsed result from a bulk parameter fetch (REQ_GET_ALL_PARAMS, 0xA0).
/// </summary>
public class BulkParams
{
    // Header (offset 0, 16 bytes)
    public byte FormatVersion;
    public byte PlatformId;       // 0=RP2040, 1=RP2350
    public byte NumChannels;      // total (11)
    public byte NumOutputChannels; // outputs (9)
    public byte NumInputChannels;  // inputs (2)
    public byte MaxBands;          // bands per channel (12)

    // Global (offset 16, 16 bytes)
    public float PreampGainDb;
    public bool Bypass;
    public bool LoudnessEnabled;
    public float LoudnessRefSpl;
    public float LoudnessIntensityPct;

    // Crossfeed (offset 32, 16 bytes)
    public bool CrossfeedEnabled;
    public byte CrossfeedPreset;
    public bool CrossfeedItd;
    public float CrossfeedFreq;
    public float CrossfeedFeedDb;

    // Delays (offset 64, 44 bytes) — float[11]
    public float[] Delays = Array.Empty<float>();

    // Matrix crosspoints (offset 108, 144 bytes) — [input, output]
    public (bool enabled, bool invert, float gain)[,] Crosspoints = new (bool, bool, float)[2, 9];

    // Outputs (offset 252, 108 bytes) — 9 × 12 bytes
    public (bool enabled, bool muted, float gain, float delay)[] Outputs =
        new (bool, bool, float, float)[9];

    // Pin config (offset 360, 8 bytes)
    public byte NumPinOutputs;
    public byte[] Pins = Array.Empty<byte>();

    // EQ bands (offset 368, 2112 bytes) — [channel, band] 11×12×16
    public FilterParams[,] Eq = new FilterParams[11, 12];

    // Channel names (offset 2480, 352 bytes) — 11 × 32-char strings
    public string[] ChannelNames = Array.Empty<string>();

    // I2S config (offset 2832, 16 bytes) — present when packet >= 2848
    public byte[] OutputSlotTypes = new byte[4]; // per-slot: 0=S/PDIF, 1=I2S
    public byte BckPin;
    public byte MckPin;
    public bool MckEnabled;
    public byte MckMultiplierEncoded; // 0=128x, 1=256x
    public bool HasI2SConfig;

    // Volume leveller (offset 2848, 16 bytes) — present when packet >= 2864
    public bool LevellerEnabled;
    public byte LevellerSpeed;       // 0=Slow, 1=Medium, 2=Fast
    public bool LevellerLookahead;
    public float LevellerAmount;     // 0-100
    public float LevellerMaxGainDb;  // 0-35
    public float LevellerGateDb;     // -96 to 0
    public bool HasLevellerConfig;

    // Per-channel preamp (offset 2864, 16 bytes) — V6+, present when packet >= 2872
    public float PreampLDb;
    public float PreampRDb;
    public bool HasPerChannelPreamp;

    // Master volume (offset 2880, 16 bytes) — V6+, present when packet >= 2884
    public float MasterVolumeDb;
    public bool HasMasterVolume;

    // Input source config (offset 2896, 16 bytes) — V7+, present when packet >= 2898
    public byte InputSource;        // 0 = USB, 1 = S/PDIF
    public byte SpdifRxPin;          // GPIO pin (informational; not applied via bulk SET)
    public bool HasInputConfig;

    // LG Sound Sync (offset 2912, 16 bytes) — V8+, present when packet >= 2928.
    // Only the user-writable `enabled` flag is captured; the runtime
    // observation fields (present, volume, muted) are diagnostic-only and
    // not part of preset state.
    public bool LgSoundSyncEnabled;
    public bool HasLgSoundSync;

    // User volume / vendor mute (offset 2928, 16 bytes) — V9+, present
    // when packet >= 2944. UserVolumeDb mirrors the UAC1 host slider's
    // dB value; UserMute is the standalone vendor mute (independent of
    // UAC1 mute state — see Documentation/current_architecture.md).
    public float UserVolumeDb;
    public bool UserMute;
    public bool HasUserVolume;

    // External DAC hardware mute (offset 2944, 16 bytes) — V10+, present
    // when packet >= 2960. Decoded via DacHwMuteConfig.TryParse so the
    // wire layout lives in one place.
    public DacHwMuteConfig? DacHwMute;
    public bool HasDacHwMute;
}

/// <summary>
/// Parses the bulk parameter packet from firmware. V2 is 2832 bytes; V3+ grows
/// with trailing optional sections (I2S, leveller, per-channel preamp, master
/// volume, input source, LG Sound Sync, user volume / mute, DAC hardware
/// mute). V10 is 2960 bytes. Accept any version &gt;= V2 so newer firmwares
/// aren't rejected, and key feature presence off the actual transfer length
/// so older firmware still works.
/// </summary>
public static class BulkParamsParser
{
    public const int PacketSize = 2832;         // V2 minimum payload
    public const int PacketSizeV7 = 2912;       // V7 payload (V2 + I2S + leveller + preamp + master + input)
    public const int PacketSizeV10 = 2960;      // V10 payload (V7 + LG + user vol + DAC HW mute)
    public const byte MinFormatVersion = 2;

    // Section offsets
    private const int OffsetHeader = 0;
    private const int OffsetGlobal = 16;
    private const int OffsetCrossfeed = 32;
    // offset 48 = legacy channels (ignored)
    private const int OffsetDelays = 64;
    private const int OffsetCrosspoints = 108;
    private const int OffsetOutputs = 252;
    private const int OffsetPinConfig = 360;
    internal const int OffsetEq = 368;           // Start of WireBandParams[11][12]; exposed for notify-endpoint dispatch.
    internal const int WireBandSize = 16;        // sizeof(WireBandParams)
    internal const int WireMaxChannels = 11;
    internal const int WireMaxBands = 12;
    private const int OffsetChannelNames = 2480;

    public static BulkParams? Parse(byte[] buffer)
    {
        if (buffer == null || buffer.Length < PacketSize)
            return null;

        var p = new BulkParams();

        // ── Header (16 bytes) ──
        p.FormatVersion = buffer[OffsetHeader + 0];
        if (p.FormatVersion < MinFormatVersion)
            return null;

        p.PlatformId = buffer[OffsetHeader + 1];
        p.NumChannels = buffer[OffsetHeader + 2];
        p.NumOutputChannels = buffer[OffsetHeader + 3];
        p.NumInputChannels = buffer[OffsetHeader + 4];
        p.MaxBands = buffer[OffsetHeader + 5];

        // ── Global (16 bytes) ──
        p.PreampGainDb = BitConverter.ToSingle(buffer, OffsetGlobal + 0);
        p.Bypass = buffer[OffsetGlobal + 4] != 0;
        p.LoudnessEnabled = buffer[OffsetGlobal + 5] != 0;
        // bytes 6-7 reserved
        p.LoudnessRefSpl = BitConverter.ToSingle(buffer, OffsetGlobal + 8);
        p.LoudnessIntensityPct = BitConverter.ToSingle(buffer, OffsetGlobal + 12);

        // ── Crossfeed (16 bytes) ──
        p.CrossfeedEnabled = buffer[OffsetCrossfeed + 0] != 0;
        p.CrossfeedPreset = buffer[OffsetCrossfeed + 1];
        p.CrossfeedItd = buffer[OffsetCrossfeed + 2] != 0;
        // byte 3 reserved
        p.CrossfeedFreq = BitConverter.ToSingle(buffer, OffsetCrossfeed + 4);
        p.CrossfeedFeedDb = BitConverter.ToSingle(buffer, OffsetCrossfeed + 8);

        // ── Delays (44 bytes = 11 × float) ──
        p.Delays = new float[11];
        for (int i = 0; i < 11; i++)
            p.Delays[i] = BitConverter.ToSingle(buffer, OffsetDelays + i * 4);

        // ── Crosspoints (144 bytes = 2 inputs × 9 outputs × 8 bytes) ──
        // Each crosspoint: enabled(1), invert(1), reserved(2), gain(4)
        p.Crosspoints = new (bool, bool, float)[2, 9];
        for (int inp = 0; inp < 2; inp++)
        {
            for (int outp = 0; outp < 9; outp++)
            {
                int off = OffsetCrosspoints + (inp * 9 + outp) * 8;
                bool enabled = buffer[off + 0] != 0;
                bool invert = buffer[off + 1] != 0;
                float gain = BitConverter.ToSingle(buffer, off + 4);
                p.Crosspoints[inp, outp] = (enabled, invert, gain);
            }
        }

        // ── Outputs (108 bytes = 9 × 12 bytes) ──
        // Each: enabled(1), mute(1), reserved(2), gain(4), delay(4)
        p.Outputs = new (bool, bool, float, float)[9];
        for (int o = 0; o < 9; o++)
        {
            int off = OffsetOutputs + o * 12;
            bool enabled = buffer[off + 0] != 0;
            bool muted = buffer[off + 1] != 0;
            float gain = BitConverter.ToSingle(buffer, off + 4);
            float delay = BitConverter.ToSingle(buffer, off + 8);
            p.Outputs[o] = (enabled, muted, gain, delay);
        }

        // ── Pin config (8 bytes) ──
        p.NumPinOutputs = buffer[OffsetPinConfig + 0];
        p.Pins = new byte[5];
        for (int i = 0; i < 5; i++)
            p.Pins[i] = buffer[OffsetPinConfig + 1 + i];

        // ── EQ bands (2112 bytes = 11 channels × 12 bands × 16 bytes) ──
        // Each band: type(1), bypass(1), reserved(2), freq(4), Q(4), gain(4)
        // Bypass byte is firmware 1.1.4+; older firmware leaves byte at 0 (active).
        // Firmware normalizes to 0 or 1 on collect, so strict == 1 is safe.
        p.Eq = new FilterParams[WireMaxChannels, WireMaxBands];
        for (int ch = 0; ch < WireMaxChannels; ch++)
        {
            for (int band = 0; band < WireMaxBands; band++)
            {
                int off = OffsetEq + (ch * WireMaxBands + band) * WireBandSize;
                p.Eq[ch, band] = ParseBand(buffer, off);
            }
        }

        // ── Channel names (352 bytes = 11 × 32-char null-terminated strings) ──
        p.ChannelNames = new string[11];
        for (int ch = 0; ch < 11; ch++)
        {
            int off = OffsetChannelNames + ch * 32;
            int len = 0;
            while (len < 32 && buffer[off + len] != 0) len++;
            p.ChannelNames[ch] = Encoding.UTF8.GetString(buffer, off, len);
        }

        // ── I2S config (16 bytes, optional) ──
        const int OffsetI2S = 2832;
        if (buffer.Length >= OffsetI2S + 16)
        {
            p.HasI2SConfig = true;
            for (int i = 0; i < 4; i++)
                p.OutputSlotTypes[i] = buffer[OffsetI2S + i];
            p.BckPin = buffer[OffsetI2S + 4];
            p.MckPin = buffer[OffsetI2S + 5];
            p.MckEnabled = buffer[OffsetI2S + 6] != 0;
            p.MckMultiplierEncoded = buffer[OffsetI2S + 7];
            // bytes 8-15 reserved
        }

        // ── Volume leveller (16 bytes, optional) ──
        const int OffsetLeveller = 2848;
        if (buffer.Length >= OffsetLeveller + 16)
        {
            p.HasLevellerConfig = true;
            p.LevellerEnabled = buffer[OffsetLeveller] != 0;
            p.LevellerSpeed = buffer[OffsetLeveller + 1];
            p.LevellerLookahead = buffer[OffsetLeveller + 2] != 0;
            // byte 3 reserved
            p.LevellerAmount = BitConverter.ToSingle(buffer, OffsetLeveller + 4);
            p.LevellerMaxGainDb = BitConverter.ToSingle(buffer, OffsetLeveller + 8);
            p.LevellerGateDb = BitConverter.ToSingle(buffer, OffsetLeveller + 12);
        }

        // ── Per-channel preamp (16 bytes, V6+) ──
        const int OffsetPreamp = 2864;
        if (p.FormatVersion >= 6 && buffer.Length >= OffsetPreamp + 8)
        {
            p.HasPerChannelPreamp = true;
            p.PreampLDb = BitConverter.ToSingle(buffer, OffsetPreamp + 0);
            p.PreampRDb = BitConverter.ToSingle(buffer, OffsetPreamp + 4);
        }

        // ── Master volume (16 bytes, V6+) ──
        const int OffsetMasterVol = 2880;
        if (p.FormatVersion >= 6 && buffer.Length >= OffsetMasterVol + 4)
        {
            p.HasMasterVolume = true;
            p.MasterVolumeDb = BitConverter.ToSingle(buffer, OffsetMasterVol + 0);
        }

        // ── Input source config (16 bytes, V7+) ──
        // WireInputConfig: input_source(1), spdif_rx_pin(1), reserved[14]
        const int OffsetInputCfg = 2896;
        if (p.FormatVersion >= 7 && buffer.Length >= OffsetInputCfg + 2)
        {
            p.HasInputConfig = true;
            p.InputSource = buffer[OffsetInputCfg + 0];
            p.SpdifRxPin = buffer[OffsetInputCfg + 1];
        }

        // ── LG Sound Sync (16 bytes, V8+) ──
        // WireLgSoundSync: enabled(1), present(1), volume(1), muted(1),
        // reserved[12]. Only `enabled` is preset state; runtime fields
        // (present, volume, muted) are diagnostic and not captured here.
        const int OffsetLgSoundSync = 2912;
        if (p.FormatVersion >= 8 && buffer.Length >= OffsetLgSoundSync + 16)
        {
            p.HasLgSoundSync = true;
            p.LgSoundSyncEnabled = buffer[OffsetLgSoundSync + 0] != 0;
        }

        // ── User volume / vendor mute (16 bytes, V9+) ──
        // WireUserVolume: user_volume_db(float, 4), user_mute(1), reserved[11]
        const int OffsetUserVolume = 2928;
        if (p.FormatVersion >= 9 && buffer.Length >= OffsetUserVolume + 16)
        {
            p.HasUserVolume = true;
            p.UserVolumeDb = BitConverter.ToSingle(buffer, OffsetUserVolume + 0);
            p.UserMute = buffer[OffsetUserVolume + 4] != 0;
        }

        // ── External DAC hardware mute (16 bytes, V10+) ──
        // WireDacHwMute: enabled(1), active_low(1), pin(1), reserved0(1),
        // hold_ms(uint16 LE), release_ms(uint16 LE), reserved[8]. Decoded
        // via DacHwMuteConfig.TryParse so the wire layout (LE uint16s, the
        // reserved0 alignment byte, the 0xFF "no pin" sentinel) is defined
        // in exactly one place.
        const int OffsetDacHwMute = 2944;
        if (p.FormatVersion >= 10 && buffer.Length >= OffsetDacHwMute + DacHwMuteConfig.WireSize)
        {
            p.DacHwMute = DacHwMuteConfig.TryParse(buffer, OffsetDacHwMute);
            p.HasDacHwMute = p.DacHwMute != null;
        }

        return p;
    }

    /// <summary>
    /// Parse a single 16-byte WireBandParams entry at the given buffer offset.
    /// Shared between the bulk-params path and the notify-endpoint path so
    /// both decode the wire layout identically (incl. the bypass byte at +1).
    /// </summary>
    internal static FilterParams ParseBand(byte[] buffer, int offset)
    {
        return new FilterParams
        {
            Type = (FilterType)buffer[offset + 0],
            Bypass = buffer[offset + 1] == 1,
            Frequency = BitConverter.ToSingle(buffer, offset + 4),
            Q = BitConverter.ToSingle(buffer, offset + 8),
            Gain = BitConverter.ToSingle(buffer, offset + 12)
        };
    }
}
