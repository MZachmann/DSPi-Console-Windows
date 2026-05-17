using System.Collections.ObjectModel;
using System.Text;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Captures a snapshot of all DSP state for change detection.
/// </summary>
public class PresetSnapshot
{
    public float InputPreampLDb;
    public float InputPreampRDb;
    public float MasterVolumeDb;
    // V15+ preset slot persists audio_state.volume as user_vol_index. Captured
    // unconditionally — unlike MasterVolumeDb, user volume isn't gated by a
    // mode flag, the firmware always restores it on preset load (if the slot
    // is V15+; pre-V15 slots leave it untouched, which is correct legacy
    // behavior, but new slots written by current firmware always include it).
    public float UserVolumeDb;
    public bool Bypass;

    public bool LoudnessEnabled;
    public float LoudnessRefSpl;
    public float LoudnessIntensity;

    public bool CrossfeedEnabled;
    public int CrossfeedPreset;
    public float CrossfeedFreq;
    public float CrossfeedFeed;
    public bool CrossfeedItd;

    public Dictionary<int, float> Delays = new();
    public bool[,] MatrixRouting = new bool[2, 9];
    public float[,] MatrixGain = new float[2, 9];
    public bool[,] MatrixInvert = new bool[2, 9];

    public Dictionary<int, bool> OutputEnabled = new();
    public bool[] OutputMuted = new bool[9];
    public Dictionary<int, float> OutputGains = new();

    public FilterParams[,] Eq = new FilterParams[11, 12];
    public Dictionary<int, string> ChannelNames = new();

    public Dictionary<int, byte> OutputPins = new();
    public byte[] OutputSlotTypes = new byte[4];

    public byte I2SBckPin;
    public bool MckEnabled;
    public byte MckPin;
    public int MckMultiplier;

    // Input source (V7+ wire format, V13+ slot data) — saved with each preset.
    public InputSource InputSource;

    // SPDIF RX pin: V13+ slots persist it alongside the output pins, and
    // apply_slot_to_live restores it when the directory's include_pins flag
    // is on (flash_storage.c:695). Tracked the same way as OutputPins —
    // captured unconditionally; the include_pins flag only controls what
    // preset_load applies.
    public byte SpdifRxPin;

    // LG Sound Sync enable (V8+ preset slot field). Only the user-writable
    // `enabled` flag is preset state; runtime fields (present, volume, muted)
    // are diagnostic and not captured. Tracks via REQ_SET/GET_LG_SOUND_SYNC
    // (0xE6/0xE7) when the toggle moves and is honored on bulk SET.
    public bool LgSoundSyncEnabled;

    /// <summary>
    /// Capture a snapshot from the current ViewModel state.
    /// </summary>
    public static PresetSnapshot Capture(MainViewModel vm)
    {
        var snap = new PresetSnapshot
        {
            InputPreampLDb = vm.InputPreampLDb,
            InputPreampRDb = vm.InputPreampRDb,
            MasterVolumeDb = vm.MasterVolumeDb,
            UserVolumeDb = vm.UserVolumeDb,
            Bypass = vm.Bypass,
            LoudnessEnabled = vm.LoudnessEnabled,
            LoudnessRefSpl = vm.LoudnessRefSPL,
            LoudnessIntensity = vm.LoudnessIntensity,
            CrossfeedEnabled = vm.CrossfeedEnabled,
            CrossfeedPreset = vm.CrossfeedPreset,
            CrossfeedFreq = vm.CrossfeedFreq,
            CrossfeedFeed = vm.CrossfeedFeed,
            CrossfeedItd = vm.CrossfeedItd,
        };

        // Delays and gains
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            snap.Delays[id] = vm.GetChannelDelay(ch);
            if (ch.IsOutput)
                snap.OutputGains[id] = vm.GetChannelGain(ch);
        }

        // Matrix
        var outputs = vm.ActiveOutputs;
        for (int inp = 0; inp < 2; inp++)
        {
            for (int o = 0; o < outputs.Count && o < 9; o++)
            {
                snap.MatrixRouting[inp, o] = vm.GetMatrixRouting(inp, o);
                snap.MatrixGain[inp, o] = vm.GetMatrixGain(inp, o);
                snap.MatrixInvert[inp, o] = vm.GetMatrixInvert(inp, o);
            }
        }

        // Output enabled/muted
        for (int o = 0; o < outputs.Count && o < 9; o++)
        {
            snap.OutputEnabled[o] = vm.IsOutputEnabled(o);
            snap.OutputMuted[o] = vm.GetOutputMuted(o);
        }

        // EQ bands
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            var filters = vm.GetFilters(ch);
            for (int b = 0; b < filters.Count && b < 12; b++)
            {
                snap.Eq[id, b] = filters[b].Clone();
            }
        }

        // Channel names
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            var name = vm.GetChannelName(ch);
            if (name != ch.Name)
                snap.ChannelNames[id] = name;
        }

        // Pin assignments (per pin-output id) and output slot types
        for (int id = 0; id < 5; id++)
            snap.OutputPins[id] = vm.GetOutputPinValue(id);
        for (int s = 0; s < snap.OutputSlotTypes.Length; s++)
            snap.OutputSlotTypes[s] = (byte)vm.GetOutputSlotType(s);

        // I2S hardware config
        snap.I2SBckPin = vm.I2SBckPin;
        snap.MckEnabled = vm.MckEnabled;
        snap.MckPin = vm.MckPin;
        snap.MckMultiplier = vm.MckMultiplier;

        // Input source (preset state on V7+/V13+ firmware; harmless USB default otherwise)
        snap.InputSource = vm.ActiveInputSource;

        // SPDIF RX pin (V13+ slot data — persists alongside output_pins)
        snap.SpdifRxPin = vm.SpdifRxPin;

        // LG Sound Sync enable flag (V8+ preset slot field)
        snap.LgSoundSyncEnabled = vm.LgSoundSyncEnabled;

        return snap;
    }
}

/// <summary>
/// Computes a human-readable diff between two PresetSnapshots.
/// </summary>
public static class PresetDiff
{
    private const int MaxLines = 15;

    private static string FormatDb(float v) => $"{v:F1} dB";

    private static string FormatVal(float v) =>
        v == (int)v && Math.Abs(v) < 100000 ? $"{(int)v}" : $"{v:F1}";

    public static List<string> Diff(PresetSnapshot old, PresetSnapshot cur, MainViewModel vm)
    {
        var changes = new List<string>();

        // Global
        if (Math.Abs(old.InputPreampLDb - cur.InputPreampLDb) > 0.05f)
            changes.Add($"Input L preamp: {FormatDb(old.InputPreampLDb)} → {FormatDb(cur.InputPreampLDb)}");
        if (Math.Abs(old.InputPreampRDb - cur.InputPreampRDb) > 0.05f)
            changes.Add($"Input R preamp: {FormatDb(old.InputPreampRDb)} → {FormatDb(cur.InputPreampRDb)}");
        // Master volume only participates in preset dirty state when the firmware
        // is configured to store it with each preset. In independent mode it is
        // managed separately via "Save Master Volume".
        if (vm.MasterVolumeMode == 1 &&
            Math.Abs(old.MasterVolumeDb - cur.MasterVolumeDb) > 0.05f)
        {
            string FormatMv(float v) => v <= -127.5f ? "mute" : FormatDb(v);
            changes.Add($"Master volume: {FormatMv(old.MasterVolumeDb)} → {FormatMv(cur.MasterVolumeDb)}");
        }
        // User volume is unconditionally per-preset on V15+ firmware. 0.5 dB
        // threshold (vs. 0.05 elsewhere) absorbs the firmware's int-dB
        // quantization — the slot stores vol_index in whole-dB steps, so a
        // saved -12.0 reloads as exactly -12.0; we only care about real
        // user-driven moves, not sub-dB ripples from notification echoes.
        if (Math.Abs(old.UserVolumeDb - cur.UserVolumeDb) > 0.5f)
            changes.Add($"User volume: {FormatDb(old.UserVolumeDb)} → {FormatDb(cur.UserVolumeDb)}");
        if (old.Bypass != cur.Bypass)
            changes.Add($"Master EQ bypass: {(old.Bypass ? "on" : "off")} \u2192 {(cur.Bypass ? "on" : "off")}");

        // Loudness
        if (old.LoudnessEnabled != cur.LoudnessEnabled)
            changes.Add($"Loudness: {(cur.LoudnessEnabled ? "enabled" : "disabled")}");
        if (Math.Abs(old.LoudnessRefSpl - cur.LoudnessRefSpl) > 0.05f)
            changes.Add($"Loudness ref SPL: {FormatVal(old.LoudnessRefSpl)} \u2192 {FormatVal(cur.LoudnessRefSpl)}");
        if (Math.Abs(old.LoudnessIntensity - cur.LoudnessIntensity) > 0.05f)
            changes.Add($"Loudness intensity: {FormatVal(old.LoudnessIntensity)}% \u2192 {FormatVal(cur.LoudnessIntensity)}%");

        // Crossfeed
        if (old.CrossfeedEnabled != cur.CrossfeedEnabled)
            changes.Add($"Crossfeed: {(cur.CrossfeedEnabled ? "enabled" : "disabled")}");
        if (old.CrossfeedPreset != cur.CrossfeedPreset)
            changes.Add($"Crossfeed preset: {old.CrossfeedPreset} \u2192 {cur.CrossfeedPreset}");
        if (Math.Abs(old.CrossfeedFreq - cur.CrossfeedFreq) > 0.5f)
            changes.Add($"Crossfeed frequency: {FormatVal(old.CrossfeedFreq)} \u2192 {FormatVal(cur.CrossfeedFreq)} Hz");
        if (Math.Abs(old.CrossfeedFeed - cur.CrossfeedFeed) > 0.05f)
            changes.Add($"Crossfeed feed: {FormatDb(old.CrossfeedFeed)} \u2192 {FormatDb(cur.CrossfeedFeed)}");
        if (old.CrossfeedItd != cur.CrossfeedItd)
            changes.Add($"Crossfeed ITD: {(cur.CrossfeedItd ? "enabled" : "disabled")}");

        // Channel delays
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            float oldD = old.Delays.TryGetValue(id, out var od) ? od : 0;
            float curD = cur.Delays.TryGetValue(id, out var cd) ? cd : 0;
            if (Math.Abs(oldD - curD) > 0.00005f)
            {
                var name = vm.GetChannelName(ch);
                changes.Add($"{name} delay: {FormatVal(oldD)} ms \u2192 {FormatVal(curD)} ms");
            }
        }

        // Matrix crosspoints
        var outputs = vm.ActiveOutputs;
        int crosspointChanges = 0;
        for (int inp = 0; inp < 2; inp++)
        {
            for (int o = 0; o < outputs.Count && o < 9; o++)
            {
                if (old.MatrixRouting[inp, o] != cur.MatrixRouting[inp, o] ||
                    old.MatrixInvert[inp, o] != cur.MatrixInvert[inp, o] ||
                    Math.Abs(old.MatrixGain[inp, o] - cur.MatrixGain[inp, o]) > 0.005f)
                    crosspointChanges++;
            }
        }
        if (crosspointChanges > 0)
            changes.Add($"{crosspointChanges} crosspoint{(crosspointChanges == 1 ? "" : "s")} changed");

        // Output settings (enabled, muted, gain)
        for (int o = 0; o < outputs.Count && o < 9; o++)
        {
            var name = vm.GetChannelName(outputs[o]);
            bool oldEn = old.OutputEnabled.TryGetValue(o, out var oe) && oe;
            bool curEn = cur.OutputEnabled.TryGetValue(o, out var ce) && ce;
            if (oldEn != curEn)
                changes.Add($"{name}: {(curEn ? "enabled" : "disabled")}");
            if (old.OutputMuted[o] != cur.OutputMuted[o])
                changes.Add($"{name}: {(cur.OutputMuted[o] ? "muted" : "unmuted")}");

            int chId = (int)outputs[o].Id;
            float oldG = old.OutputGains.TryGetValue(chId, out var og) ? og : 0;
            float curG = cur.OutputGains.TryGetValue(chId, out var cg) ? cg : 0;
            if (Math.Abs(oldG - curG) > 0.005f)
                changes.Add($"{name} gain: {FormatDb(oldG)} \u2192 {FormatDb(curG)}");
        }

        // EQ bands per channel
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            int bandChanges = 0;
            for (int b = 0; b < ch.BandCount && b < 12; b++)
            {
                var oldF = old.Eq[id, b];
                var curF = cur.Eq[id, b];
                if (oldF == null && curF == null) continue;
                if (oldF == null || curF == null || !oldF.Equals(curF))
                    bandChanges++;
            }
            if (bandChanges > 0)
            {
                var name = vm.GetChannelName(ch);
                changes.Add($"{bandChanges} band{(bandChanges == 1 ? "" : "s")} changed on {name}");
            }
        }

        // Channel names
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            string oldName = old.ChannelNames.TryGetValue(id, out var on) ? on : ch.Name;
            string curName = cur.ChannelNames.TryGetValue(id, out var cn) ? cn : ch.Name;
            if (oldName != curName)
                changes.Add($"{oldName} \u2192 {curName}");
        }

        // Pin assignments
        int pinChanges = 0;
        foreach (var key in old.OutputPins.Keys)
        {
            byte oldPin = old.OutputPins[key];
            byte curPin = cur.OutputPins.TryGetValue(key, out var cp) ? cp : (byte)0;
            if (oldPin != curPin) pinChanges++;
        }
        if (pinChanges > 0)
            changes.Add($"{pinChanges} pin assignment{(pinChanges == 1 ? "" : "s")} changed");

        // Output slot types (SPDIF/I2S)
        int slotChanges = 0;
        for (int s = 0; s < old.OutputSlotTypes.Length; s++)
            if (old.OutputSlotTypes[s] != cur.OutputSlotTypes[s]) slotChanges++;
        if (slotChanges > 0)
            changes.Add($"{slotChanges} output slot type{(slotChanges == 1 ? "" : "s")} changed");

        // I2S hardware config
        if (old.I2SBckPin != cur.I2SBckPin)
            changes.Add($"I2S BCK pin: {old.I2SBckPin} → {cur.I2SBckPin}");
        if (old.MckEnabled != cur.MckEnabled)
            changes.Add($"MCK: {(cur.MckEnabled ? "enabled" : "disabled")}");
        if (old.MckPin != cur.MckPin)
            changes.Add($"MCK pin: {old.MckPin} → {cur.MckPin}");
        if (old.MckMultiplier != cur.MckMultiplier)
            changes.Add($"MCK multiplier: {old.MckMultiplier}x → {cur.MckMultiplier}x");

        // Input source
        if (old.InputSource != cur.InputSource)
        {
            string Name(InputSource s) => s == InputSource.Spdif ? "S/PDIF" : "USB";
            changes.Add($"Input source: {Name(old.InputSource)} → {Name(cur.InputSource)}");
        }

        // SPDIF RX pin
        if (old.SpdifRxPin != cur.SpdifRxPin)
            changes.Add($"S/PDIF RX pin: GPIO {old.SpdifRxPin} → GPIO {cur.SpdifRxPin}");

        // LG Sound Sync
        if (old.LgSoundSyncEnabled != cur.LgSoundSyncEnabled)
            changes.Add($"LG Sound Sync: {(cur.LgSoundSyncEnabled ? "enabled" : "disabled")}");

        return changes;
    }

    /// <summary>
    /// Format a list of changes as a bullet-point summary string.
    /// </summary>
    public static string FormatSummary(List<string> changes)
    {
        if (changes.Count == 0)
            return "No changes detected.";

        var sb = new StringBuilder();
        int shown = Math.Min(changes.Count, MaxLines);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append("\u2022 ");
            sb.Append(changes[i]);
        }

        int remaining = changes.Count - shown;
        if (remaining > 0)
        {
            sb.AppendLine();
            sb.Append($"...and {remaining} more change{(remaining == 1 ? "" : "s")}");
        }

        return sb.ToString();
    }
}
