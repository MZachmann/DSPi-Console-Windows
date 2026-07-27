using System;

namespace DSPiConsole.Core.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Control Surfaces + IR remote (firmware control_surfaces.h; caps v4, config v2,
// IR config v1). Physical GPIO controls (buttons, switches, pots, encoders, LEDs,
// PWM LEDs) and an IR receiver with learned remote commands, each bound to a DSP
// "noun" (parameter) + "action" (verb). All wire structs are packed, little-endian.
//
// The whole editor is CAPS-DRIVEN: the firmware serves a capabilities header, a
// per-type action/pin table, and a per-noun descriptor table over 0x86; the host
// builds its pickers from those rather than hardcoding the noun/type tables (they
// are append-only and versioned). Only a client-side display-name table for nouns
// is baked in here (the wire format carries no strings for them).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Physical control component (firmware CsType). Index into the caps
/// type table.</summary>
public enum CsType : byte
{
    None = 0, Button = 1, Switch = 2, Pot = 3, Encoder = 4,
    Led = 5, LedPwm = 6, Ir = 7
}

/// <summary>DSP parameter a control drives or reflects (firmware CsNoun, 0..48).
/// The picker reads which nouns are available (and their ranges/units/targets)
/// live from caps — this enum is for the few places that special-case a noun.</summary>
public enum CsNoun : byte
{
    UserVolume = 0, MasterVolume = 1, UserMute = 2, Loudness = 3, Crossfeed = 4,
    Leveller = 5, Preset = 6, InputSource = 7, Clip = 8, EqBypass = 9,
    LgSync = 10, CrossfeedPreset = 11, CrossfeedItd = 12, LevellerAmount = 13,
    LevellerSpeed = 14, LevellerLookahead = 15, Preamp = 16, OutputGain = 17,
    OutputMute = 18, OutputEnable = 19, FilterFreq = 20, FilterGain = 21,
    FilterQ = 22, FilterType = 23, FilterBypass = 24, Siggen = 25,
    DacMuteTest = 26, ClipCh = 27, Level = 28, SpdifLock = 29, SampleRate = 30,
    UsbStreaming = 31, AdatActive = 32, LgPresent = 33, LgMuted = 34,
    // caps v4 additions. The six upmixer nouns are RP2350-only (action mask 0
    // on RP2040, the same convention as AdatActive).
    Upmix = 35, UpmixCenterMode = 36, UpmixSurroundMode = 37, UpmixStrength = 38,
    UpmixWidth = 39, UpmixPresence = 40, Psybass = 41, PsybassCutoff = 42,
    PsybassHarmonics = 43, PsybassDrive = 44, PsybassCharacter = 45,
    PsybassOriginal = 46, OutputDelay = 47, PresetReload = 48
}

/// <summary>Verb a control performs (firmware CsAction). Value = bit position;
/// caps action masks use <c>1 &lt;&lt; action</c>.</summary>
public enum CsAction : byte
{
    Adjust = 0,     // pot: absolute position → value range
    Step = 1,       // encoder: ± step per detent
    Inc = 2,        // button: + step per press
    Dec = 3,        // button: - step per press
    Toggle = 4,     // button: invert a bool
    Set = 5,        // button: set noun to `value`
    Follow = 6,     // switch: bool tracks switch position
    Trigger = 7,    // button: fire the noun's command
    IndEquals = 8,  // LED: lit while value == `value`
    Momentary = 9,  // button: set to `value` while held, restore on release
    IndAbove = 10,  // LED: lit while value >= `value`
    IndLevel = 11   // PWM LED: brightness follows value across range
}

/// <summary>Button gesture (firmware CsEvent); 0 for non-button types.</summary>
public enum CsEvent : byte { Press = 0, Long = 1, Double = 2 }

/// <summary>CsBinding.flags / IrCommand.flags bitfield.</summary>
[Flags]
public enum CsFlags : byte
{
    None = 0,
    Invert = 0x01,   // input active-high w/ pull-down; LED active-low
    Reverse = 0x02,  // pot / encoder: invert direction
    Wrap = 0x04,     // enum STEP/INC/DEC wraps around
    Accel = 0x08,    // encoder only: fast rotation multiplies step
    Repeat = 0x10    // button INC/DEC on press: auto-repeat while held
}

/// <summary>Noun value shape (firmware CS_KIND_*).</summary>
public enum CsKind : byte { Continuous = 0, Bool = 1, Enum = 2 }

/// <summary>Noun unit; fixes the wire encoding of value/range/step and the
/// stepping law (firmware CS_UNIT_*). <c>Ms</c> is a caps-v4 addition (8.8
/// milliseconds, linear, default step 0.1 ms) used by OutputDelay.</summary>
public enum CsUnit : byte { None = 0, Db = 1, Hz = 2, Q = 3, Percent = 4, Ms = 5 }

/// <summary>What a noun's <c>target</c> addresses (firmware CS_TARGET_*).</summary>
public enum CsTarget : byte
{
    None = 0, InputCh = 1, OutputCh = 2, DspCh = 3, DspBand = 4
}

/// <summary>Pin capability required by a type (firmware CS_PINCLASS_*).</summary>
public enum CsPinClass : byte { Any = 0, Adc = 1 }

/// <summary>IR protocol of a learned code (firmware CS_IR_PROTO_*).</summary>
public enum CsIrProto : byte { None = 0, Nec = 1, Rc5 = 2, Rc6 = 3, Hash = 4 }

/// <summary>REQ_CS_IR_LEARN wValue action.</summary>
public static class CsIrLearnAction
{
    public const ushort Cancel = 0;
    public const ushort Arm = 1;
    public const ushort Read = 2;
}

/// <summary>IR learn engine state (firmware CS_IR_LEARN_*).</summary>
public enum CsIrLearnState : byte { Idle = 0, Armed = 1, Done = 2, Timeout = 3 }

/// <summary>Control-surface deferred-apply status codes. 0x00..0x05 reuse the
/// shared PIN_CONFIG_* namespace (see Usb PinConfigResult); 0x10..0x1E are the
/// CS extension.</summary>
public static class CsStatus
{
    public const byte Success = 0x00;      // PIN_CONFIG_SUCCESS
    public const byte InvalidPin = 0x01;   // (CS: also "encoder's two pins equal")
    public const byte PinInUse = 0x02;

    public const byte InvalidSlot = 0x10;
    public const byte InvalidType = 0x11;
    public const byte InvalidNoun = 0x12;
    public const byte InvalidAction = 0x13;
    public const byte InvalidValue = 0x14;
    public const byte PinNotAdc = 0x15;
    public const byte Pending = 0x16;      // accepted, apply not yet run — poll again
    public const byte InvalidTarget = 0x17;
    public const byte InvalidEvent = 0x18;
    public const byte PwmConflict = 0x19;
    public const byte EventInUse = 0x1A;
    public const byte Busy = 0x1B;
    public const byte FlashError = 0x1C;
    public const byte IrInUse = 0x1D;
    public const byte NoIr = 0x1E;

    /// <summary>Human-readable message for a CS status code.</summary>
    public static string Message(byte code) => code switch
    {
        Success => "OK",
        InvalidPin => "Invalid pin (or encoder pins are equal)",
        PinInUse => "Pin already in use by another peripheral",
        InvalidSlot => "Invalid slot",
        InvalidType => "Invalid component type",
        InvalidNoun => "Parameter not available on this device",
        InvalidAction => "Action not valid for this parameter",
        InvalidValue => "Invalid value",
        PinNotAdc => "This control needs an ADC pin (GPIO 26–28)",
        Pending => "Applying…",
        InvalidTarget => "Invalid channel/band target",
        InvalidEvent => "Invalid button event",
        PwmConflict => "PWM slice already driven by another LED",
        EventInUse => "That button gesture is already bound on this pin",
        Busy => "Device busy — try again",
        FlashError => "Flash write failed",
        IrInUse => "An IR receiver is already configured",
        NoIr => "No IR receiver is active",
        _ => $"Error 0x{code:X2}"
    };
}

/// <summary>Shared constants for the control-surface feature.</summary>
public static class CsLimits
{
    public const int MaxBindings = 16;
    public const int MaxIrCommands = 8;
    public const int NameLen = 32;          // per-slot name buffer, NUL-terminated
    public const byte GpioUnused = 0xFF;
    public const ushort CapsAll = 0xFFFF;   // 0x86 wValue selecting the caps header
    public const byte LastSlotSave = 0xFF;  // CsStatusPacket.LastSlot for save/revert
    public const byte LastSlotIrFlag = 0x80;// high bit of LastSlot marks an IR sub-slot
    public const byte NdfDeferred = 0x01;   // CsNounDesc.dflags

    /// <summary>The only GPIOs a pot may occupy (ADC0..2). GPIO 29 (VSYS) excluded.</summary>
    public static readonly byte[] AdcPins = { 26, 27, 28 };
}

// ─────────────────────────────────────────────────────────────────────────────
// Fixed-point wire encoding helpers (firmware unit encoding; mirror the macOS
// csEncode*/csDecode* helpers verbatim — the 8.8 encoding and "step in octaves
// for Hz/Q" law are load-bearing).
// ─────────────────────────────────────────────────────────────────────────────
public static class CsWire
{
    /// <summary>Units whose value/range are 8.8 signed fixed point (1.0 = 256).</summary>
    public static bool UnitIsFixedPoint(CsUnit u) =>
        u is CsUnit.Db or CsUnit.Q or CsUnit.Percent or CsUnit.Ms;

    /// <summary>Encode a value/range operand for a noun's unit. DB/Q/PERCENT/MS
    /// use 8.8; NONE and HZ are plain integers.</summary>
    public static short EncodeValue(double v, CsUnit u) =>
        (short)Math.Round(UnitIsFixedPoint(u) ? v * 256.0 : v);

    public static double DecodeValue(short raw, CsUnit u) =>
        UnitIsFixedPoint(u) ? raw / 256.0 : raw;

    /// <summary>Encode a step operand. Everything except NONE is scaled ×256 —
    /// for HZ/Q the value is in octaves (256 = 1 octave); for DB/PERCENT/MS it is
    /// a linear dB/%/ms step. NONE (bool/enum) is a plain position count.</summary>
    public static short EncodeStep(double v, CsUnit u) =>
        (short)Math.Round(u == CsUnit.None ? v : v * 256.0);

    public static double DecodeStep(short raw, CsUnit u) =>
        u == CsUnit.None ? raw : raw / 256.0;

    public static string UnitSymbol(CsUnit u) => u switch
    {
        CsUnit.Db => "dB",
        CsUnit.Hz => "Hz",
        CsUnit.Q => "Q",
        CsUnit.Percent => "%",
        CsUnit.Ms => "ms",
        _ => ""
    };

    /// <summary>The step the firmware applies when a binding leaves <c>step</c>
    /// at 0, in the unit's own terms (octaves for the log units). Display only.</summary>
    public static double DefaultStep(CsUnit u) => u switch
    {
        CsUnit.Hz or CsUnit.Q => 1.0 / 12.0,  // 1/12 octave
        CsUnit.Ms => 0.1,                     // caps v4: ms detents are finer
        CsUnit.None => 1,                     // one enum position
        _ => 1                                // 1 dB / 1 %
    };

    public static string IrProtocolName(CsIrProto p) => p switch
    {
        CsIrProto.Nec => "NEC",
        CsIrProto.Rc5 => "RC5",
        CsIrProto.Rc6 => "RC6",
        CsIrProto.Hash => "Generic",
        _ => "None"
    };
}

/// <summary>Client-side display metadata for the 49 nouns (the wire format
/// carries no strings). Kept minimal — the picker still reads availability,
/// ranges, units and targets from caps.</summary>
public static class CsNounInfo
{
    private static readonly string[] Names =
    {
        "User Volume", "Master Volume", "User Mute", "Loudness", "Crossfeed",
        "Volume Leveller", "Preset", "Input Source", "Clip", "EQ Bypass",
        "LG Sound Sync", "Crossfeed Preset", "Crossfeed ITD", "Leveller Amount",
        "Leveller Speed", "Leveller Lookahead", "Preamp", "Output Gain",
        "Output Mute", "Output Enable", "Filter Frequency", "Filter Gain",
        "Filter Q", "Filter Type", "Filter Bypass", "Signal Generator",
        "DAC Mute Test", "Clip (Channel)", "Level", "S/PDIF Lock", "Sample Rate",
        "USB Streaming", "ADAT Active", "LG Present", "LG Muted",
        // caps v4
        "Stereo Upmixer", "Upmix Centre Mode", "Upmix Surround Mode",
        "Upmix Strength", "Upmix Centre Width", "Upmix Centre Presence",
        "Psychoacoustic Bass", "Bass Cutoff Frequency", "Bass Harmonics",
        "Bass Drive", "Bass Character", "Original Bass Level",
        "Output Delay", "Reload Preset"
    };

    // Value labels for the enum-kind nouns. The picker only uses as many entries
    // as the noun's caps enum_count reports, and falls back to the bare index for
    // anything past the end, so a firmware that grows an enum still works.
    private static readonly string[] PresetNames =
        { "Preset 1", "Preset 2", "Preset 3", "Preset 4", "Preset 5",
          "Preset 6", "Preset 7", "Preset 8", "Preset 9", "Preset 10" };

    private static readonly string[] InputSourceNames =
        { "USB", "S/PDIF", "I2S", "ADAT", "S/PDIF 2", "S/PDIF 3" };

    private static readonly string[] CrossfeedPresetNames =
        { "Default", "Chu Moy", "Jan Meier", "Custom" };

    private static readonly string[] LevellerSpeedNames = { "Slow", "Medium", "Fast" };

    private static readonly string[] FilterTypeNames =
        { "Flat", "Peaking", "Low Shelf", "High Shelf", "Low Pass", "High Pass",
          "Notch", "All Pass", "All Pass (1st)", "Low Shelf (1st)",
          "High Shelf (1st)", "Linkwitz Transform" };

    private static readonly string[] SampleRateNames = { "44.1 kHz", "48 kHz", "96 kHz" };

    // Wire 0/1 and 0/1/2; the app shows the upmixer's product mode names.
    private static readonly string[] UpmixCenterModeNames = { "Sinner", "Logician" };
    private static readonly string[] UpmixSurroundModeNames = { "Off", "Sinner", "Logician" };

    /// <summary>Label for one value of an enum-kind noun, e.g. "S/PDIF" for
    /// InputSource 1. Falls back to the plain index for unknown nouns/values.</summary>
    public static string EnumLabel(int noun, int value)
    {
        var table = (CsNoun)noun switch
        {
            CsNoun.Preset => PresetNames,
            CsNoun.InputSource => InputSourceNames,
            CsNoun.CrossfeedPreset => CrossfeedPresetNames,
            CsNoun.LevellerSpeed => LevellerSpeedNames,
            CsNoun.FilterType => FilterTypeNames,
            CsNoun.SampleRate => SampleRateNames,
            CsNoun.UpmixCenterMode => UpmixCenterModeNames,
            CsNoun.UpmixSurroundMode => UpmixSurroundModeNames,
            _ => null
        };
        return table != null && value >= 0 && value < table.Length
            ? table[value]
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Name(int noun) =>
        noun >= 0 && noun < Names.Length ? Names[noun] : $"Noun {noun}";

    public static string Name(CsNoun noun) => Name((int)noun);

    /// <summary>Coarse UI grouping for the noun picker.</summary>
    public static string Group(int noun) => (CsNoun)noun switch
    {
        CsNoun.UserVolume or CsNoun.MasterVolume or CsNoun.UserMute or CsNoun.Preamp
            or CsNoun.Level => "Volume",
        CsNoun.Loudness or CsNoun.Crossfeed or CsNoun.Leveller or CsNoun.EqBypass
            or CsNoun.CrossfeedPreset or CsNoun.CrossfeedItd or CsNoun.LevellerAmount
            or CsNoun.LevellerSpeed or CsNoun.LevellerLookahead => "DSP",
        CsNoun.Upmix or CsNoun.UpmixCenterMode or CsNoun.UpmixSurroundMode
            or CsNoun.UpmixStrength or CsNoun.UpmixWidth or CsNoun.UpmixPresence => "Upmixer",
        CsNoun.Psybass or CsNoun.PsybassCutoff or CsNoun.PsybassHarmonics
            or CsNoun.PsybassDrive or CsNoun.PsybassCharacter
            or CsNoun.PsybassOriginal => "Psychoacoustic Bass",
        CsNoun.OutputGain or CsNoun.OutputMute or CsNoun.OutputEnable
            or CsNoun.OutputDelay => "Output",
        CsNoun.FilterFreq or CsNoun.FilterGain or CsNoun.FilterQ or CsNoun.FilterType
            or CsNoun.FilterBypass => "Filter",
        CsNoun.Preset or CsNoun.InputSource or CsNoun.Siggen or CsNoun.DacMuteTest
            or CsNoun.SampleRate or CsNoun.UsbStreaming or CsNoun.AdatActive
            or CsNoun.PresetReload => "System",
        CsNoun.LgSync or CsNoun.LgPresent or CsNoun.LgMuted or CsNoun.SpdifLock => "LG / S/PDIF",
        CsNoun.Clip or CsNoun.ClipCh => "Status",
        _ => "Other"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Wire structs
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The 24-byte CsBinding wire struct (REQ_SET/GET_CS_BINDING). A
/// default (all-zero) instance is the "cleared slot" blob (type = None). A
/// configured single-pin binding must set <see cref="Gpio1"/> to 0xFF.</summary>
public sealed class CsBinding
{
    public const int WireSize = 24;

    public CsType Type;         // @0
    public byte Noun;           // @1  CsNoun
    public byte Action;         // @2  CsAction
    public CsFlags Flags;       // @3
    public byte Gpio0;          // @4  primary GPIO
    public byte Gpio1 = CsLimits.GpioUnused; // @5  second GPIO (encoder); 0xFF = single-pin
    public byte Event;          // @6  CsEvent (buttons); 0 otherwise
    public byte Target;         // @7  channel address
    public byte Index;          // @8  filter band (DSP_BAND nouns)
    // @9 reserved
    public short Value;         // @10 SET/MOMENTARY / IND comparand (unit-encoded)
    public short Step;          // @12 STEP/INC/DEC size; 0 = unit default
    public short RangeMin;      // @14 pot/IND_LEVEL span low; both 0 = full range
    public short RangeMax;      // @16 pot/IND_LEVEL span high
    // @18..23 reserved2[6]

    public bool IsConfigured => Type != CsType.None;

    /// <summary>A fresh cleared-slot binding (gpio1 = 0 so the blob is all-zero).</summary>
    public static CsBinding Cleared() => new() { Gpio1 = 0 };

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = (byte)Type;
        b[1] = Noun;
        b[2] = Action;
        b[3] = (byte)Flags;
        b[4] = Gpio0;
        b[5] = Gpio1;
        b[6] = Event;
        b[7] = Target;
        b[8] = Index;
        // b[9] reserved
        BitConverter.GetBytes(Value).CopyTo(b, 10);
        BitConverter.GetBytes(Step).CopyTo(b, 12);
        BitConverter.GetBytes(RangeMin).CopyTo(b, 14);
        BitConverter.GetBytes(RangeMax).CopyTo(b, 16);
        return b;
    }

    public static CsBinding? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsBinding
        {
            Type = (CsType)d[0],
            Noun = d[1],
            Action = d[2],
            Flags = (CsFlags)d[3],
            Gpio0 = d[4],
            Gpio1 = d[5],
            Event = d[6],
            Target = d[7],
            Index = d[8],
            Value = BitConverter.ToInt16(d, 10),
            Step = BitConverter.ToInt16(d, 12),
            RangeMin = BitConverter.ToInt16(d, 14),
            RangeMax = BitConverter.ToInt16(d, 16),
        };
    }

    public CsBinding Clone() => (CsBinding)MemberwiseClone();

    /// <summary>Wire-equality: compares the serialized bytes (ignores any
    /// reserved padding differences, which are always zero).</summary>
    public bool WireEquals(CsBinding other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>The 16-byte IrCommand wire struct (REQ_SET/GET_CS_IR_CMD). A
/// button-shaped command fired by a learned code instead of a GPIO edge. There
/// is no gpio/event field — the receiver's pin lives on the container CsBinding
/// (CS_TYPE_IR). An occupied sub-slot with <see cref="Code"/> == 0 is rejected.</summary>
public sealed class IrCommand
{
    public const int WireSize = 16;

    public byte Noun;           // @0  CsNoun
    public byte Action;         // @1  CsAction (button subset: Inc/Dec/Toggle/Set/Trigger/Momentary)
    public CsFlags Flags;       // @2  Wrap | Repeat only
    public byte Target;         // @3  channel address
    public byte Index;          // @4  filter band
    public CsIrProto Proto;     // @5  0 = empty sub-slot
    public short Value;         // @6  SET/MOMENTARY target (unit-encoded)
    public short Step;          // @8  INC/DEC size; 0 = unit default
    // @10,11 reserved
    public uint Code;           // @12 learned code, LE; 0 = never learned

    public bool IsConfigured => Proto != CsIrProto.None;

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = Noun;
        b[1] = Action;
        b[2] = (byte)Flags;
        b[3] = Target;
        b[4] = Index;
        b[5] = (byte)Proto;
        BitConverter.GetBytes(Value).CopyTo(b, 6);
        BitConverter.GetBytes(Step).CopyTo(b, 8);
        // b[10],b[11] reserved
        BitConverter.GetBytes(Code).CopyTo(b, 12);
        return b;
    }

    public static IrCommand? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new IrCommand
        {
            Noun = d[0],
            Action = d[1],
            Flags = (CsFlags)d[2],
            Target = d[3],
            Index = d[4],
            Proto = (CsIrProto)d[5],
            Value = BitConverter.ToInt16(d, 6),
            Step = BitConverter.ToInt16(d, 8),
            Code = BitConverter.ToUInt32(d, 12),
        };
    }

    public IrCommand Clone() => (IrCommand)MemberwiseClone();

    public bool WireEquals(IrCommand other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Display label for the learned code, e.g. "NEC 0x00FF00FF".</summary>
    public string CodeLabel => IsConfigured
        ? $"{CsWire.IrProtocolName(Proto)} 0x{Code:X8}"
        : "Not learned";
}

/// <summary>One entry in the caps type table (4 bytes; CsTypeDesc).</summary>
public readonly struct CsTypeDesc
{
    public readonly ushort Actions;   // CS_ACT_BIT mask
    public readonly byte PinCount;    // 1 or 2
    public readonly CsPinClass PinClass;

    public CsTypeDesc(ushort actions, byte pinCount, CsPinClass pinClass)
    {
        Actions = actions; PinCount = pinCount; PinClass = pinClass;
    }

    public bool SupportsAction(CsAction a) => (Actions & (1u << (int)a)) != 0;
}

/// <summary>The caps header + type table (REQ_GET_CS_CAPS, wValue 0xFFFF). Length
/// is variable — the v3 tail (<see cref="MaxIrCommands"/>) sits at
/// <c>4 + 4*TypeCount</c> so a future type-table growth won't move it. Parse
/// defensively rather than assuming 40 bytes.</summary>
public sealed class CsCapsHeader
{
    public byte CapsVersion;
    public byte MaxBindings;
    public byte TypeCount;
    public byte NounCount;
    public CsTypeDesc[] Types = Array.Empty<CsTypeDesc>();
    public byte MaxIrCommands;   // v3; 0 on a v2 header = IR unavailable

    public bool IsValid => TypeCount > 0 && NounCount > 0;

    public static CsCapsHeader? FromBytes(byte[] d)
    {
        if (d == null || d.Length < 4) return null;
        var h = new CsCapsHeader
        {
            CapsVersion = d[0],
            MaxBindings = d[1],
            TypeCount = d[2],
            NounCount = d[3],
        };
        int count = h.TypeCount;
        // Only parse as many type descriptors as actually fit in the buffer.
        int maxFit = (d.Length - 4) / 4;
        if (count > maxFit) count = maxFit;
        h.Types = new CsTypeDesc[count];
        for (int i = 0; i < count; i++)
        {
            int off = 4 + i * 4;
            h.Types[i] = new CsTypeDesc(
                BitConverter.ToUInt16(d, off),
                d[off + 2],
                (CsPinClass)d[off + 3]);
        }
        int irOff = 4 + h.TypeCount * 4;
        h.MaxIrCommands = irOff < d.Length ? d[irOff] : (byte)0;
        return h;
    }

    /// <summary>Type descriptor for a CsType, or null if out of range.</summary>
    public CsTypeDesc? DescFor(CsType t)
    {
        int i = (int)t;
        return i >= 0 && i < Types.Length ? Types[i] : null;
    }
}

/// <summary>One noun's descriptor (12 bytes; CsNounDesc), from 0x86 wValue=noun.</summary>
public sealed class CsNounDesc
{
    public const int WireSize = 12;

    public CsKind Kind;
    public byte EnumCount;
    public ushort Actions;     // accepted-action mask; 0 = unavailable on platform
    public short MinQ;         // continuous range low, unit-encoded
    public short MaxQ;         // continuous range high
    public CsUnit Unit;
    public CsTarget TargetKind;
    public byte TargetCount;   // valid targets 0..count-1; 0 = untargeted
    public byte DFlags;

    public bool IsAvailable => Actions != 0;
    public bool IsTargeted => TargetKind != CsTarget.None && TargetCount > 0;
    public bool HasBand => TargetKind == CsTarget.DspBand;
    public bool SupportsAction(CsAction a) => (Actions & (1u << (int)a)) != 0;
    public double Min => CsWire.DecodeValue(MinQ, Unit);
    public double Max => CsWire.DecodeValue(MaxQ, Unit);

    public static CsNounDesc? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsNounDesc
        {
            Kind = (CsKind)d[0],
            EnumCount = d[1],
            Actions = BitConverter.ToUInt16(d, 2),
            MinQ = BitConverter.ToInt16(d, 4),
            MaxQ = BitConverter.ToInt16(d, 6),
            Unit = (CsUnit)d[8],
            TargetKind = (CsTarget)d[9],
            TargetCount = d[10],
            DFlags = d[11],
        };
    }
}

/// <summary>The 32-byte CsStatusPacket (REQ_GET_CS_STATUS). The base v2 layout is
/// 22 bytes; the IR tail (bytes 22..31) is only read when present.</summary>
public sealed class CsStatusPacket
{
    public byte LastStatus;      // most recent deferred CS SET result
    public byte LastSlot;        // slot; 0x80|n = IR sub-slot; 0xFF = save/revert
    public byte MaxBindings;
    public bool Dirty;           // v3; live differs from flash
    public ushort ActiveMask;    // bit N = binding N live
    public byte[] SlotStatus = new byte[16];
    public byte IrActiveMask;    // v3; bit N = IR command N live
    public CsIrLearnState IrLearnState; // v3
    public byte[] IrCmdStatus = new byte[8]; // v3

    public bool IsSlotActive(int slot) =>
        slot >= 0 && slot < 16 && (ActiveMask & (1 << slot)) != 0;

    public byte SlotHealth(int slot) =>
        slot >= 0 && slot < SlotStatus.Length ? SlotStatus[slot] : (byte)0;

    public bool IsIrCmdActive(int sub) =>
        sub >= 0 && sub < 8 && (IrActiveMask & (1 << sub)) != 0;

    public byte IrCmdHealth(int sub) =>
        sub >= 0 && sub < IrCmdStatus.Length ? IrCmdStatus[sub] : (byte)0;

    public static CsStatusPacket? FromBytes(byte[] d)
    {
        if (d == null || d.Length < 22) return null;
        var s = new CsStatusPacket
        {
            LastStatus = d[0],
            LastSlot = d[1],
            MaxBindings = d[2],
            Dirty = d[3] != 0,
            ActiveMask = BitConverter.ToUInt16(d, 4),
        };
        Array.Copy(d, 6, s.SlotStatus, 0, 16);
        if (d.Length >= 24)
        {
            s.IrActiveMask = d[22];
            s.IrLearnState = (CsIrLearnState)d[23];
        }
        if (d.Length >= 32)
            Array.Copy(d, 24, s.IrCmdStatus, 0, 8);
        return s;
    }
}

/// <summary>The 8-byte IR-learn result (REQ_CS_IR_LEARN wValue=2).</summary>
public sealed class CsIrLearnResult
{
    public const int WireSize = 8;

    public CsIrLearnState State;
    public CsIrProto Proto;
    public uint Code;   // LE

    public bool IsDone => State == CsIrLearnState.Done;
    public bool IsTimeout => State == CsIrLearnState.Timeout;

    public static CsIrLearnResult? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsIrLearnResult
        {
            State = (CsIrLearnState)d[0],
            Proto = (CsIrProto)d[1],
            Code = BitConverter.ToUInt32(d, 4),
        };
    }
}
