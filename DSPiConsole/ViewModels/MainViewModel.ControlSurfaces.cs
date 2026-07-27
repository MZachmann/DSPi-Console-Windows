using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Control Surfaces + IR remote state and device orchestration (firmware
/// 0x84–0x8F, 0x9D/0x9E). The whole editor is caps-driven: we probe the caps
/// header + per-noun descriptors once, then read the 16 binding slots, 16 slot
/// names, and 8 IR command sub-slots.
///
/// <para>Three-tier persistence: every SET is a live-only <b>preview</b> that
/// applies immediately (device <c>Dirty</c>=true) but is RAM-only; <see cref="CsSave"/>
/// writes the whole config to flash; <see cref="CsRevert"/> discards the preview.
/// A local clean baseline gives net-zero dirty suppression so add-then-remove
/// doesn't strand the Save bar.</para>
///
/// <para>These methods block (they poll the deferred-apply status) — call them
/// off the UI thread (the window wraps them in <c>Task.Run</c>, matching the
/// ADAT settings page).</para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>True once the caps probe (0x86) answers. Older firmware STALLs,
    /// which hides the whole feature.</summary>
    [ObservableProperty]
    private bool _controlSurfacesSupported;

    private CsCapsHeader? _csCaps;
    private CsNounDesc?[] _csNounDescs = Array.Empty<CsNounDesc?>();
    private CsBinding[] _csBindings = NewClearedBindings();
    private string[] _csNames = NewEmptyNames();
    private IrCommand[] _csIrCommands = NewEmptyIrCommands();
    private CsStatusPacket? _csStatus;

    // Local clean baseline (wire bytes) captured whenever the device reports a
    // non-dirty state — used for net-zero dirty suppression.
    private CsBinding[]? _csCleanBindings;
    private string[]? _csCleanNames;
    private IrCommand[]? _csCleanIrCommands;

    public CsCapsHeader? CsCaps => _csCaps;
    public IReadOnlyList<CsNounDesc?> CsNounDescs => _csNounDescs;
    public IReadOnlyList<CsBinding> CsBindings => _csBindings;
    public IReadOnlyList<string> CsNames => _csNames;
    public IReadOnlyList<IrCommand> CsIrCommands => _csIrCommands;
    public CsStatusPacket? CsStatus => _csStatus;

    /// <summary>Number of usable binding slots (min of caps + local cap of 16).</summary>
    public int CsSlotCount => Math.Min((int)(_csCaps?.MaxBindings ?? CsLimits.MaxBindings), CsLimits.MaxBindings);

    /// <summary>Number of usable IR command sub-slots.</summary>
    public int CsIrMax => Math.Min((int)(_csCaps?.MaxIrCommands ?? 0), CsLimits.MaxIrCommands);

    /// <summary>Whether IR remote commands are available (a receiver type exists
    /// and the firmware advertises IR command slots).</summary>
    public bool CsIrSupported =>
        _csCaps != null && _csCaps.MaxIrCommands > 0 && _csCaps.TypeCount > (int)CsType.Ir;

    /// <summary>Per-noun descriptor, or null if the noun index is out of range /
    /// unavailable on this platform.</summary>
    public CsNounDesc? CsNounDescFor(int noun) =>
        noun >= 0 && noun < _csNounDescs.Length ? _csNounDescs[noun] : null;

    /// <summary>Unsaved-changes flag: the firmware's sticky dirty bit AND an actual
    /// difference from the local clean baseline (so add-then-remove nets to clean).</summary>
    public bool CsDirty => _csStatus?.Dirty == true && !MatchesCleanBaseline();

    /// <summary>Raised (on the dispatcher) after the whole config is (re)loaded from
    /// the device — the window rebuilds its cards in response.</summary>
    public event Action? ControlSurfacesReloaded;

    private static CsBinding[] NewClearedBindings()
    {
        var a = new CsBinding[CsLimits.MaxBindings];
        for (int i = 0; i < a.Length; i++) a[i] = CsBinding.Cleared();
        return a;
    }

    private static string[] NewEmptyNames()
    {
        var a = new string[CsLimits.MaxBindings];
        for (int i = 0; i < a.Length; i++) a[i] = "";
        return a;
    }

    private static IrCommand[] NewEmptyIrCommands()
    {
        var a = new IrCommand[CsLimits.MaxIrCommands];
        for (int i = 0; i < a.Length; i++) a[i] = new IrCommand();
        return a;
    }

    /// <summary>Probe the feature and read the whole live config: caps header,
    /// per-noun descriptors, all binding slots + names, and IR command sub-slots.
    /// Sets <see cref="ControlSurfacesSupported"/> (false if the firmware STALLs).
    /// Blocking — call off the UI thread.</summary>
    public void FetchControlSurfaces()
    {
        var caps = _device.GetCsCaps();
        if (caps == null || !caps.IsValid)
        {
            _dispatcher.TryEnqueue(() => ControlSurfacesSupported = false);
            return;
        }

        var descs = new CsNounDesc?[caps.NounCount];
        for (int n = 0; n < caps.NounCount; n++)
            descs[n] = _device.GetCsNounDesc(n);

        int slots = Math.Min((int)caps.MaxBindings, CsLimits.MaxBindings);
        var bindings = NewClearedBindings();
        var names = NewEmptyNames();
        for (int s = 0; s < slots; s++)
        {
            bindings[s] = _device.GetCsBinding(s) ?? CsBinding.Cleared();
            names[s] = _device.GetCsName(s);
        }

        var irCmds = NewEmptyIrCommands();
        int irMax = Math.Min((int)caps.MaxIrCommands, CsLimits.MaxIrCommands);
        for (int i = 0; i < irMax; i++)
            irCmds[i] = _device.GetCsIrCommand(i) ?? new IrCommand();

        var status = _device.GetCsStatus();

        _csCaps = caps;
        _csNounDescs = descs;
        _csBindings = bindings;
        _csNames = names;
        _csIrCommands = irCmds;
        _csStatus = status;
        if (status != null && !status.Dirty) CaptureCsCleanBaseline();

        _dispatcher.TryEnqueue(() =>
        {
            ControlSurfacesSupported = true;
            OnPropertyChanged(nameof(CsStatus));
            OnPropertyChanged(nameof(CsDirty));
            ControlSurfacesReloaded?.Invoke();
        });
    }

    /// <summary>Re-read the 32-byte status packet (active/dirty masks, per-slot
    /// health) into <see cref="CsStatus"/>.</summary>
    public void RefreshCsStatus()
    {
        var status = _device.GetCsStatus();
        if (status == null) return;
        _csStatus = status;
        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(CsStatus));
            OnPropertyChanged(nameof(CsDirty));
        });
    }

    /// <summary>Stage a binding (live preview) and re-read the slot + status.
    /// Returns the firmware CS status byte.</summary>
    public byte SetCsBinding(int slot, CsBinding binding)
    {
        byte result = _device.SetCsBinding(slot, binding);
        _csBindings[slot] = _device.GetCsBinding(slot) ?? CsBinding.Cleared();
        RefreshCsStatus();
        return result;
    }

    /// <summary>Stage a slot name (live preview). Empty string clears it.</summary>
    public byte SetCsName(int slot, string name)
    {
        byte result = _device.SetCsName(slot, name ?? "");
        _csNames[slot] = _device.GetCsName(slot);
        RefreshCsStatus();
        return result;
    }

    /// <summary>Stage an IR command sub-slot (live preview) and re-read it.</summary>
    public byte SetCsIrCommand(int sub, IrCommand cmd)
    {
        byte result = _device.SetCsIrCommand(sub, cmd);
        _csIrCommands[sub] = _device.GetCsIrCommand(sub) ?? new IrCommand();
        RefreshCsStatus();
        return result;
    }

    /// <summary>Persist the whole live config to flash. On success re-captures the
    /// clean baseline so the Save bar clears.</summary>
    public byte CsSave()
    {
        byte result = _device.CsSave();
        RefreshCsStatus();
        if (result == DSPiConsole.Core.Models.CsStatus.Success) CaptureCsCleanBaseline();
        _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(CsDirty)));
        return result;
    }

    /// <summary>Discard the live preview, reload the stored config from flash, and
    /// re-read every slot / IR command / name.</summary>
    public byte CsRevert()
    {
        byte result = _device.CsRevert();

        int slots = CsSlotCount;
        for (int s = 0; s < slots; s++)
        {
            _csBindings[s] = _device.GetCsBinding(s) ?? CsBinding.Cleared();
            _csNames[s] = _device.GetCsName(s);
        }
        int irMax = CsIrMax;
        for (int i = 0; i < irMax; i++)
            _csIrCommands[i] = _device.GetCsIrCommand(i) ?? new IrCommand();

        var status = _device.GetCsStatus();
        _csStatus = status;
        if (status != null && !status.Dirty) CaptureCsCleanBaseline();

        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(CsStatus));
            OnPropertyChanged(nameof(CsDirty));
            ControlSurfacesReloaded?.Invoke();
        });
        return result;
    }

    /// <summary>Display name for a binding's channel target, using the same
    /// user-editable names the sidebar shows (so a rename is reflected here).
    /// Falls back to a plain positional label if the firmware advertises a
    /// target the app's channel model doesn't cover.</summary>
    public string CsTargetLabel(CsTarget kind, int index)
    {
        var ch = CsTargetChannel(kind, index);
        if (ch != null) return GetChannelName(ch);
        return kind switch
        {
            CsTarget.InputCh => $"Input {index + 1}",
            CsTarget.OutputCh => $"Output {index + 1}",
            _ => $"Channel {index + 1}",
        };
    }

    /// <summary>The app channel a CS target addresses, or null if out of range.
    /// INPUT_CH indexes the wire input region, OUTPUT_CH the output position, and
    /// DSP_CH / DSP_BAND the unified channel space (inputs then outputs).</summary>
    private Channel? CsTargetChannel(CsTarget kind, int index)
    {
        if (index < 0) return null;
        return kind switch
        {
            CsTarget.InputCh => index < Channel.AllInputs.Count ? Channel.AllInputs[index] : null,
            CsTarget.OutputCh => index < ActiveOutputs.Count ? ActiveOutputs[index] : null,
            CsTarget.DspCh or CsTarget.DspBand =>
                ChannelForAppId(ChannelMap.WireToApp(index, _device.NumInputChannels)),
            _ => null,
        };
    }

    /// <summary>App channel id → its <see cref="Channel"/>. Outputs resolve through
    /// <see cref="ActiveOutputs"/> so RP2040 gets its own PDM entry rather than the
    /// SPDIF 3 L channel that shares the id.</summary>
    private Channel? ChannelForAppId(int appId)
    {
        if (appId < 0) return null;
        if (appId < ChannelMap.AppInputCount) return Channel.AllInputs[appId];
        if (appId >= ChannelMap.ExtraInputFirstId)
        {
            int i = ChannelMap.AppInputCount + (appId - ChannelMap.ExtraInputFirstId);
            return i < Channel.AllInputs.Count ? Channel.AllInputs[i] : null;
        }
        int pos = appId - ChannelMap.AppInputCount;
        return pos < ActiveOutputs.Count ? ActiveOutputs[pos] : null;
    }

    /// <summary>Arm IR learn (10 s window). False if no live IR receiver.</summary>
    public bool CsIrLearnArm() => _device.CsIrLearnArm();

    /// <summary>Cancel a pending IR learn.</summary>
    public void CsIrLearnCancel() => _device.CsIrLearnCancel();

    /// <summary>Poll the IR-learn result.</summary>
    public CsIrLearnResult? CsIrLearnRead() => _device.CsIrLearnRead();

    private void CaptureCsCleanBaseline()
    {
        _csCleanBindings = new CsBinding[_csBindings.Length];
        for (int i = 0; i < _csBindings.Length; i++) _csCleanBindings[i] = _csBindings[i].Clone();
        _csCleanNames = (string[])_csNames.Clone();
        _csCleanIrCommands = new IrCommand[_csIrCommands.Length];
        for (int i = 0; i < _csIrCommands.Length; i++) _csCleanIrCommands[i] = _csIrCommands[i].Clone();
    }

    private bool MatchesCleanBaseline()
    {
        if (_csCleanBindings == null || _csCleanNames == null || _csCleanIrCommands == null)
            return false; // no baseline yet → treat firmware dirty as authoritative
        for (int i = 0; i < _csBindings.Length; i++)
            if (!_csBindings[i].WireEquals(_csCleanBindings[i])) return false;
        for (int i = 0; i < _csNames.Length; i++)
            if (!string.Equals(_csNames[i], _csCleanNames[i], StringComparison.Ordinal)) return false;
        for (int i = 0; i < _csIrCommands.Length; i++)
            if (!_csIrCommands[i].WireEquals(_csCleanIrCommands[i])) return false;
        return true;
    }
}
