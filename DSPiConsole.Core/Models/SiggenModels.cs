using System;

namespace DSPiConsole.Core.Models;

/// <summary>Onboard test-signal generator types (firmware SiggenType, siggen.h).</summary>
public enum SiggenType : byte
{
    Sine = 0, Square = 1, White = 2, Pink = 3,
    SweepLog = 4, SweepLin = 5, SweepStep = 6,
    Impulse = 7, ClicksAlt = 8, Polarity = 9,
    ToneBurst = 10, TonePair = 11, Multitone = 12, Isp = 13, ChannelId = 14
}

/// <summary>How duration/repeat/gap are interpreted for a signal type.</summary>
public enum SiggenTimingModel : byte { Continuous = 0, Sweep = 1, Pattern = 2 }

/// <summary>Meaning of a per-type parameter slot (SiggenParamDesc.semantic).</summary>
public enum SiggenParamSemantic : byte
{
    Unused = 0, FreqHz = 1, Ms = 2, Cycles = 3, Count = 4, Ratio = 5, Pattern = 6
}

/// <summary>Generator run state (SiggenStatus.state).</summary>
public enum SiggenState : byte { Idle = 0, FadeIn = 1, Run = 2, Gap = 3, FadeOut = 4 }

/// <summary>Control actions (REQ_SIGGEN_CONTROL wValue).</summary>
public static class SiggenControl
{
    public const byte Stop = 0;
    public const byte Start = 1;
    public const byte StopNow = 2;
}

/// <summary>Config flag bits (SiggenConfig.flags).</summary>
[Flags]
public enum SiggenFlags : byte { None = 0, Raw = 0x01, Decorrelate = 0x02, Walk = 0x04 }

/// <summary>
/// The 36-byte SiggenConfig wire struct (REQ_SIGGEN_SET/GET_CONFIG). All fields
/// little-endian; version is always 1. duration_ms/repeat/gap_ms are reinterpreted
/// per the signal type's timing model; p1..p4 are per-type params (see caps).
/// </summary>
public sealed class SiggenConfig
{
    public const int WireSize = 36;
    public const byte Version = 1;
    public const float LevelMinDb = -120f;
    public const float LevelMaxDb = 0f;

    /// <summary>Level used when the device hands back an unconfigured (zeroed) config.
    /// A zeroed struct means 0 dBFS, so never adopt it as the draft's starting level.</summary>
    public const float DefaultLevelDb = -20f;

    /// <summary>Sweep length used when a sweep type has no duration yet. The firmware
    /// rejects SET_CONFIG for a sweep with duration_ms == 0.</summary>
    public const uint DefaultSweepMs = 1000;

    /// <summary>Per-channel dwell used for a walked continuous signal when none is set
    /// (matches the firmware's own 2 s fallback in compute_derived).</summary>
    public const uint DefaultWalkDwellMs = 2000;

    public SiggenType SignalType = SiggenType.Sine;
    public ushort ChannelMask;
    public ushort InvertMask;
    public SiggenFlags Flags = SiggenFlags.None;
    public float LevelDb = -20f;
    public uint DurationMs;
    public ushort Repeat;
    public ushort GapMs;
    public float P1;
    public float P2;
    public float P3;
    public float P4;

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = Version;
        b[1] = (byte)SignalType;
        BitConverter.GetBytes(ChannelMask).CopyTo(b, 2);
        BitConverter.GetBytes(InvertMask).CopyTo(b, 4);
        b[6] = (byte)Flags;
        // b[7] reserved 0
        BitConverter.GetBytes(LevelDb).CopyTo(b, 8);
        BitConverter.GetBytes(DurationMs).CopyTo(b, 12);
        BitConverter.GetBytes(Repeat).CopyTo(b, 16);
        BitConverter.GetBytes(GapMs).CopyTo(b, 18);
        BitConverter.GetBytes(P1).CopyTo(b, 20);
        BitConverter.GetBytes(P2).CopyTo(b, 24);
        BitConverter.GetBytes(P3).CopyTo(b, 28);
        BitConverter.GetBytes(P4).CopyTo(b, 32);
        return b;
    }

    /// <summary>True when the device has never staged a config: SET_CONFIG rejects an
    /// empty channel mask, so a zero mask can only come from the firmware's zeroed
    /// "no config applied" response.</summary>
    public bool IsUnconfigured => ChannelMask == 0;

    public static SiggenConfig? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        if (d[0] != Version) return null;   // unknown struct version: don't misparse
        return new SiggenConfig
        {
            SignalType = (SiggenType)d[1],
            ChannelMask = BitConverter.ToUInt16(d, 2),
            InvertMask = BitConverter.ToUInt16(d, 4),
            Flags = (SiggenFlags)d[6],
            LevelDb = BitConverter.ToSingle(d, 8),
            DurationMs = BitConverter.ToUInt32(d, 12),
            Repeat = BitConverter.ToUInt16(d, 16),
            GapMs = BitConverter.ToUInt16(d, 18),
            P1 = BitConverter.ToSingle(d, 20),
            P2 = BitConverter.ToSingle(d, 24),
            P3 = BitConverter.ToSingle(d, 28),
            P4 = BitConverter.ToSingle(d, 32),
        };
    }

    public SiggenConfig Clone() => (SiggenConfig)MemberwiseClone();

    /// <summary>Copy all fields into an existing instance (preserves its reference).</summary>
    public void CopyTo(SiggenConfig dst)
    {
        dst.SignalType = SignalType; dst.ChannelMask = ChannelMask; dst.InvertMask = InvertMask;
        dst.Flags = Flags; dst.LevelDb = LevelDb; dst.DurationMs = DurationMs;
        dst.Repeat = Repeat; dst.GapMs = GapMs; dst.P1 = P1; dst.P2 = P2; dst.P3 = P3; dst.P4 = P4;
    }

    /// <summary>Get/set a param slot 0..3 by index (P1..P4).</summary>
    public float GetParam(int i) => i switch { 0 => P1, 1 => P2, 2 => P3, _ => P4 };
    public void SetParam(int i, float v)
    {
        switch (i) { case 0: P1 = v; break; case 1: P2 = v; break; case 2: P3 = v; break; default: P4 = v; break; }
    }
}

/// <summary>The 16-byte SiggenStatus wire struct (REQ_SIGGEN_GET_STATUS).</summary>
public sealed class SiggenStatus
{
    public const int WireSize = 16;
    public SiggenState State;
    public SiggenType SignalType;
    public byte ActiveChannel;   // 0xFF when not walking
    public uint ElapsedMs;
    public ushort CyclesDone;
    public byte StopReason;
    public float CurrentFreq;

    public bool IsRunning => State is SiggenState.FadeIn or SiggenState.Run or SiggenState.Gap or SiggenState.FadeOut;

    public static SiggenStatus? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        if (d[0] != SiggenConfig.Version) return null;
        return new SiggenStatus
        {
            State = (SiggenState)d[1],
            SignalType = (SiggenType)d[2],
            ActiveChannel = d[3],
            ElapsedMs = BitConverter.ToUInt32(d, 4),
            CyclesDone = BitConverter.ToUInt16(d, 8),
            StopReason = d[10],
            CurrentFreq = BitConverter.ToSingle(d, 12),
        };
    }
}

/// <summary>One per-type parameter descriptor (13 bytes; SiggenParamDesc).</summary>
public readonly struct SiggenParamDesc
{
    public readonly SiggenParamSemantic Semantic;
    public readonly float Min;
    public readonly float Max;
    public readonly float Default;
    public bool IsUsed => Semantic != SiggenParamSemantic.Unused;

    public SiggenParamDesc(SiggenParamSemantic sem, float min, float max, float def)
    {
        Semantic = sem; Min = min; Max = max; Default = def;
    }
}

/// <summary>One signal type's descriptor (62 bytes; SiggenTypeDesc): short name,
/// timing model, and 4 parameter descriptors.</summary>
public sealed class SiggenTypeDesc
{
    public const int WireSize = 62;
    public SiggenType Id;
    public string Name = "";
    public SiggenTimingModel TimingModel;
    public SiggenParamDesc[] Params = new SiggenParamDesc[4];

    public static SiggenTypeDesc? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        var desc = new SiggenTypeDesc
        {
            Id = (SiggenType)d[0],
            Name = System.Text.Encoding.ASCII.GetString(d, 1, 8).TrimEnd('\0'),
            TimingModel = (SiggenTimingModel)d[9],
        };
        for (int p = 0; p < 4; p++)
        {
            int off = 10 + p * 13;
            desc.Params[p] = new SiggenParamDesc(
                (SiggenParamSemantic)d[off],
                BitConverter.ToSingle(d, off + 1),
                BitConverter.ToSingle(d, off + 5),
                BitConverter.ToSingle(d, off + 9));
        }
        return desc;
    }

    /// <summary>True when the generator steps through the masked channels one at a time.
    /// CHANNEL_ID always walks, whatever the WALK flag says.</summary>
    public bool WalksWith(SiggenFlags flags) =>
        Id == SiggenType.ChannelId || flags.HasFlag(SiggenFlags.Walk);

    /// <summary>
    /// Bring a draft's p1..p4 in line with this type: unused slots go to 0, and a slot
    /// that is NaN or outside the advertised range takes the descriptor default. Params
    /// carry over when the type changes, and the firmware silently substitutes its own
    /// fallbacks for out-of-range values, so seeding here keeps what the UI shows equal
    /// to what actually plays.
    /// </summary>
    public void SeedParams(SiggenConfig cfg)
    {
        for (int i = 0; i < Params.Length; i++)
        {
            var p = Params[i];
            if (!p.IsUsed) { cfg.SetParam(i, 0f); continue; }
            float v = cfg.GetParam(i);
            if (float.IsNaN(v) || v < p.Min || v > p.Max) v = p.Default;
            cfg.SetParam(i, v);
        }
    }

    /// <summary>
    /// Bring duration/repeat/gap in line with this type's timing model, zeroing the
    /// fields the model ignores so a stale value from a previously selected type cannot
    /// leak into the wire config (e.g. a pattern's gap silently spacing out sweeps).
    /// </summary>
    public void SeedTiming(SiggenConfig cfg, SiggenFlags flags)
    {
        switch (TimingModel)
        {
            case SiggenTimingModel.Sweep:
                // duration_ms = one sweep and must be > 0 or SET_CONFIG is rejected.
                if (cfg.DurationMs == 0) cfg.DurationMs = SiggenConfig.DefaultSweepMs;
                break;

            case SiggenTimingModel.Pattern:
                cfg.DurationMs = 0;   // unused
                break;

            default:  // Continuous
                if (WalksWith(flags))
                {
                    // duration_ms becomes the per-channel dwell; repeat counts passes.
                    if (cfg.DurationMs == 0) cfg.DurationMs = SiggenConfig.DefaultWalkDwellMs;
                }
                else
                {
                    cfg.Repeat = 0;   // unused
                }
                cfg.GapMs = 0;        // unused either way
                break;
        }
    }
}

/// <summary>The 8-byte SiggenCapsHeader (REQ_SIGGEN_GET_CAPS, wValue 0xFFFF).</summary>
public sealed class SiggenCaps
{
    public const int WireSize = 8;
    public byte TypeCount;
    public byte OutputChannels;   // 9 RP2350 / 5 RP2040
    public byte MultitoneMax;     // 16 RP2350 / 8 RP2040
    public ushort ValidChannelMask;

    /// <summary>Output bits the firmware accepts, falling back to OutputChannels when the
    /// header reports no mask.</summary>
    public ushort UsableChannelMask =>
        ValidChannelMask != 0 ? ValidChannelMask
        : OutputChannels >= 16 ? (ushort)0xFFFF
        : (ushort)((1 << OutputChannels) - 1);

    public static SiggenCaps? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        if (d[0] != SiggenConfig.Version) return null;
        return new SiggenCaps
        {
            TypeCount = d[1],
            OutputChannels = d[2],
            MultitoneMax = d[3],
            ValidChannelMask = BitConverter.ToUInt16(d, 4),
        };
    }
}
