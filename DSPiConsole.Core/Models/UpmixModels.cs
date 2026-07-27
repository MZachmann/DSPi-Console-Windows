using System;

namespace DSPiConsole.Core.Models;

/// <summary>Centre-engine mode (upmixer_spec.md section 1).</summary>
public enum UpmixCenterMode : byte { Passive = 0, Adaptive = 1 }

/// <summary>Surround-engine mode. Off removes rows 3-4 from the mix entirely.</summary>
public enum UpmixSurroundMode : byte { Off = 0, Passive = 1, Adaptive = 2 }

/// <summary>Param ids for REQ_UPMIX_SET/GET_PARAM (wValue). Every value travels
/// as a 4-byte float, including the enable and mode params.</summary>
public static class UpmixParam
{
    public const ushort Enabled = 0;
    public const ushort CenterMode = 1;
    public const ushort SurroundMode = 2;
    public const ushort Strength = 3;
    public const ushort CenterWidth = 4;
    public const ushort Threshold = 5;
    public const ushort Attack = 6;
    public const ushort Release = 7;
    public const ushort DetectorHpf = 8;
    public const ushort SurroundDelay = 9;
    public const ushort SurroundHpf = 10;
    public const ushort SurroundLpf = 11;
    public const ushort Decorr = 12;
    public const ushort Presence = 13;
}

/// <summary>Stereo upmixer parameter ranges and defaults (spec section 4).
/// The firmware clamps out-of-range values when computing coefficients.</summary>
public static class UpmixLimits
{
    public const float StrengthMinPct = 0f, StrengthMaxPct = 100f, StrengthDefaultPct = 100f;
    public const float WidthMinPct = 0f, WidthMaxPct = 100f, WidthDefaultPct = 25f;
    public const float ThresholdMinPct = 0f, ThresholdMaxPct = 95f, ThresholdDefaultPct = 30f;
    public const float AttackMinMs = 1f, AttackMaxMs = 500f, AttackDefaultMs = 10f;
    public const float ReleaseMinMs = 5f, ReleaseMaxMs = 2000f, ReleaseDefaultMs = 100f;
    public const float DetHpfMinHz = 20f, DetHpfMaxHz = 1000f, DetHpfDefaultHz = 200f;
    public const float SurDelayMinMs = 0f, SurDelayMaxMs = 20f, SurDelayDefaultMs = 12f;
    public const float SurHpfMinHz = 20f, SurHpfMaxHz = 2000f, SurHpfDefaultHz = 300f;
    public const float SurLpfMinHz = 1000f, SurLpfMaxHz = 20000f, SurLpfDefaultHz = 7000f;
    public const float DecorrMinPct = 0f, DecorrMaxPct = 100f, DecorrDefaultPct = 90f;
    public const float PresenceMinDb = -12f, PresenceMaxDb = 12f, PresenceDefaultDb = 0f;
}

/// <summary>
/// The 44-byte UpmixConfigPacket (REQ_UPMIX_SET/GET_CONFIG 0x4A/0x4B), byte-identical
/// to the WireUpmixParams bulk section at offset 5900 (wire V26). presence_q1 at
/// offset 3 carries the presence bell gain in 0.5 dB steps; exposed here as plain dB.
/// </summary>
public sealed class UpmixConfig
{
    public const int WireSize = 44;

    public bool Enabled;
    public byte CenterMode = (byte)UpmixCenterMode.Adaptive;
    public byte SurroundMode = (byte)UpmixSurroundMode.Adaptive;
    public float PresenceDb = UpmixLimits.PresenceDefaultDb;
    public float StrengthPct = UpmixLimits.StrengthDefaultPct;
    public float CenterWidthPct = UpmixLimits.WidthDefaultPct;
    public float CorrThresholdPct = UpmixLimits.ThresholdDefaultPct;
    public float AttackMs = UpmixLimits.AttackDefaultMs;
    public float ReleaseMs = UpmixLimits.ReleaseDefaultMs;
    public float DetectorHpfHz = UpmixLimits.DetHpfDefaultHz;
    public float SurroundDelayMs = UpmixLimits.SurDelayDefaultMs;
    public float SurroundHpfHz = UpmixLimits.SurHpfDefaultHz;
    public float SurroundLpfHz = UpmixLimits.SurLpfDefaultHz;
    public float DecorrPct = UpmixLimits.DecorrDefaultPct;

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = (byte)(Enabled ? 1 : 0);
        b[1] = CenterMode;
        b[2] = SurroundMode;
        b[3] = unchecked((byte)(sbyte)Math.Clamp(
            (int)MathF.Round(PresenceDb * 2f), -24, 24));
        BitConverter.GetBytes(StrengthPct).CopyTo(b, 4);
        BitConverter.GetBytes(CenterWidthPct).CopyTo(b, 8);
        BitConverter.GetBytes(CorrThresholdPct).CopyTo(b, 12);
        BitConverter.GetBytes(AttackMs).CopyTo(b, 16);
        BitConverter.GetBytes(ReleaseMs).CopyTo(b, 20);
        BitConverter.GetBytes(DetectorHpfHz).CopyTo(b, 24);
        BitConverter.GetBytes(SurroundDelayMs).CopyTo(b, 28);
        BitConverter.GetBytes(SurroundHpfHz).CopyTo(b, 32);
        BitConverter.GetBytes(SurroundLpfHz).CopyTo(b, 36);
        BitConverter.GetBytes(DecorrPct).CopyTo(b, 40);
        return b;
    }

    public static UpmixConfig? FromBytes(byte[] d, int offset = 0)
    {
        if (d == null || d.Length < offset + WireSize) return null;
        return new UpmixConfig
        {
            Enabled = d[offset + 0] != 0,
            CenterMode = d[offset + 1],
            SurroundMode = d[offset + 2],
            PresenceDb = (sbyte)d[offset + 3] / 2f,
            StrengthPct = BitConverter.ToSingle(d, offset + 4),
            CenterWidthPct = BitConverter.ToSingle(d, offset + 8),
            CorrThresholdPct = BitConverter.ToSingle(d, offset + 12),
            AttackMs = BitConverter.ToSingle(d, offset + 16),
            ReleaseMs = BitConverter.ToSingle(d, offset + 20),
            DetectorHpfHz = BitConverter.ToSingle(d, offset + 24),
            SurroundDelayMs = BitConverter.ToSingle(d, offset + 28),
            SurroundHpfHz = BitConverter.ToSingle(d, offset + 32),
            SurroundLpfHz = BitConverter.ToSingle(d, offset + 36),
            DecorrPct = BitConverter.ToSingle(d, offset + 40),
        };
    }
}

/// <summary>The 16-byte UpmixStatus telemetry packet (REQ_UPMIX_GET_STATUS 0x4E).
/// Fixed-point wire fields are decoded to floats.</summary>
public sealed class UpmixStatus
{
    public const int WireSize = 16;

    public bool Active;
    public byte ParkedReason;    // 0=active, 1=disabled, 2=input not stereo, 3=rate > 48 kHz
    public float Correlation;    // [-1, +1]; zero in passive centre mode
    public float Balance;        // 0 = centred, 1 = fully one-sided
    public float CenterGain;     // [0, 1] live extraction gain
    public float LsGain;         // [0, 1]
    public float RsGain;         // [0, 1]

    public static UpmixStatus? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new UpmixStatus
        {
            Active = d[0] != 0,
            ParkedReason = d[1],
            Correlation = BitConverter.ToInt16(d, 2) / 16384f,
            Balance = BitConverter.ToInt16(d, 4) / 16384f,
            CenterGain = BitConverter.ToUInt16(d, 6) / 32767f,
            LsGain = BitConverter.ToUInt16(d, 8) / 32767f,
            RsGain = BitConverter.ToUInt16(d, 10) / 32767f,
        };
    }
}
