using System;

namespace DSPiConsole.Core.Models;

/// <summary>
/// Immutable configuration for the firmware's external-DAC hardware-mute
/// feature (firmware ≥ V10, vendor opcodes 0xEA/0xEB/0xEC). Used as the unit
/// of read/write between the host UI and <c>REQ_SET_DAC_HW_MUTE_CONFIG</c>.
///
/// Wire layout (16 bytes, little-endian, matches <c>WireDacHwMute</c> in
/// <c>firmware/DSPi/bulk_params.h</c>):
/// <code>
///   byte 0  : enabled         (0=off, 1=on)
///   byte 1  : active_low      (1=assert LOW to mute, 0=assert HIGH)
///   byte 2  : pin             (GPIO; 0xFF = none)
///   byte 3  : reserved0       (zero-fill, alignment for hold_ms)
///   bytes 4-5 : hold_ms       (uint16 LE, mute-attack hold before clock-stop)
///   bytes 6-7 : release_ms    (uint16 LE, dwell after un-mute)
///   bytes 8-15 : reserved[8]  (zero-fill, future expansion)
/// </code>
///
/// Init-only setters make this value-immutable from the ViewModel's
/// perspective — mutations are expressed as new instances via <see cref="With"/>,
/// which prevents accidental in-place edits that bypass the Apply path.
/// See <c>Documentation/Features/dac_hardware_mute_spec.md</c> for protocol
/// semantics and recommended timings per DAC family.
/// </summary>
public sealed class DacHwMuteConfig : IEquatable<DacHwMuteConfig>
{
    /// <summary>Sentinel value indicating "no GPIO assigned". Matches the
    /// firmware's <c>DAC_HW_MUTE_PIN_NONE</c> in <c>dac_hw_mute.h</c>.</summary>
    public const byte PinNone = 0xFF;

    /// <summary>Wire packet size. Constant — the layout is fixed at 16 bytes
    /// for forward compatibility; future fields shrink <c>reserved[8]</c>.</summary>
    public const int WireSize = 16;

    /// <summary>Inclusive minimum for <see cref="HoldMs"/>. Firmware clamps
    /// out-of-range values silently on apply.</summary>
    public const ushort HoldMsMin = 0;

    /// <summary>Inclusive maximum for <see cref="HoldMs"/>. Per the spec, the
    /// longest verified DAC ramp is ~100 ms (WM8741 / AK4493); 500 ms gives
    /// comfortable headroom for any future addition.</summary>
    public const ushort HoldMsMax = 500;

    /// <summary>Inclusive minimum for <see cref="ReleaseMs"/>.</summary>
    public const ushort ReleaseMsMin = 0;

    /// <summary>Inclusive maximum for <see cref="ReleaseMs"/>.</summary>
    public const ushort ReleaseMsMax = 500;

    /// <summary>Feature on/off. When <c>false</c>, the firmware audio path is
    /// byte-for-byte identical to a pre-V10 build (no GPIO writes, no holds).</summary>
    public bool Enabled { get; init; }

    /// <summary>Polarity. <c>true</c> = drive the GPIO LOW to mute (typical:
    /// PCM5102A XSMT, WM8741 MUTEB). <c>false</c> = drive HIGH to mute
    /// (typical: AK4493 SMUTE with default polarity).</summary>
    public bool ActiveLow { get; init; } = true;

    /// <summary>GPIO pin number. <see cref="PinNone"/> (0xFF) means no pin is
    /// configured; the firmware silently no-ops asserts/releases in that case
    /// even when <see cref="Enabled"/> is true.</summary>
    public byte Pin { get; init; } = PinNone;

    /// <summary>Milliseconds the firmware busy-waits after asserting the mute
    /// GPIO before stopping I²S clocks. Should be ≥ the DAC's published mute
    /// ramp time (e.g. 5 ms for PCM5102A, 100 ms for WM8741 / AK4493).</summary>
    public ushort HoldMs { get; init; }

    /// <summary>Milliseconds the firmware busy-waits after releasing the mute
    /// GPIO before resuming audio. Usually 0; raise only if a specific DAC
    /// needs settle time after un-mute. Capped at <see cref="ReleaseMsMax"/>.</summary>
    public ushort ReleaseMs { get; init; }

    /// <summary>The boot/factory-reset state: feature off, active-low polarity,
    /// no pin, no holds. Identical to what <c>dac_hw_mute_init()</c> establishes
    /// on first boot of a fresh device.</summary>
    public static DacHwMuteConfig CreateDefault() => new();

    /// <summary>Produce a new instance with selected fields overridden. The
    /// receiver is unchanged. Use this from the UI's apply path:
    /// <c>var next = current.With(enabled: true, pin: 22);</c></summary>
    public DacHwMuteConfig With(
        bool? enabled = null,
        bool? activeLow = null,
        byte? pin = null,
        ushort? holdMs = null,
        ushort? releaseMs = null) => new()
    {
        Enabled = enabled ?? Enabled,
        ActiveLow = activeLow ?? ActiveLow,
        Pin = pin ?? Pin,
        HoldMs = holdMs ?? HoldMs,
        ReleaseMs = releaseMs ?? ReleaseMs,
    };

    /// <summary>Serialize to the firmware wire format. Always <see cref="WireSize"/>
    /// bytes; reserved bytes are zeroed (the firmware rejects non-zero reserved
    /// bytes on bulk-set in future versions, per the spec's forward-compat note).</summary>
    public byte[] ToWireBytes()
    {
        var data = new byte[WireSize];
        data[0] = Enabled ? (byte)1 : (byte)0;
        data[1] = ActiveLow ? (byte)1 : (byte)0;
        data[2] = Pin;
        data[3] = 0; // reserved0 — alignment for hold_ms
        data[4] = (byte)(HoldMs & 0xFF);
        data[5] = (byte)((HoldMs >> 8) & 0xFF);
        data[6] = (byte)(ReleaseMs & 0xFF);
        data[7] = (byte)((ReleaseMs >> 8) & 0xFF);
        // bytes 8..15 left at zero (reserved[8]).
        return data;
    }

    /// <summary>Parse from a firmware-returned wire buffer. Accepts buffers
    /// of <see cref="WireSize"/> or larger at the given offset (extra bytes
    /// after the section are ignored — bulk-fetch callers pass the whole
    /// REQ_GET_ALL_PARAMS packet plus the section offset). Returns
    /// <c>null</c> if the buffer is too short — caller should treat that as
    /// "DAC HW mute not supported on this firmware" (older devices STALL the
    /// GET opcode and the USB layer returns null/empty).</summary>
    public static DacHwMuteConfig? TryParse(byte[]? data, int offset = 0)
    {
        if (data == null || offset < 0 || data.Length < offset + WireSize) return null;
        return new DacHwMuteConfig
        {
            Enabled = data[offset + 0] != 0,
            ActiveLow = data[offset + 1] != 0,
            Pin = data[offset + 2],
            HoldMs = (ushort)(data[offset + 4] | (data[offset + 5] << 8)),
            ReleaseMs = (ushort)(data[offset + 6] | (data[offset + 7] << 8)),
        };
    }

    public bool Equals(DacHwMuteConfig? other) =>
        other is not null
        && Enabled == other.Enabled
        && ActiveLow == other.ActiveLow
        && Pin == other.Pin
        && HoldMs == other.HoldMs
        && ReleaseMs == other.ReleaseMs;

    public override bool Equals(object? obj) => Equals(obj as DacHwMuteConfig);

    public override int GetHashCode() =>
        HashCode.Combine(Enabled, ActiveLow, Pin, HoldMs, ReleaseMs);
}
