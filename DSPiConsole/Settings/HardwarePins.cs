using System;
using System.Collections.Generic;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;

namespace DSPiConsole.Settings;

/// <summary>
/// Cross-page pin-coordination helpers for the Hardware category.
///
/// <para>
/// Each Hardware page hosts its own pin pickers (output GPIOs, BCK/MCK,
/// SPDIF RX, DAC mute) but pins are claimed across the whole device —
/// so any one page's edit must refresh every other page's "disabled
/// because in use" labels. In the legacy <c>SettingsDialog</c> this
/// was a private method walking every picker in the same dialog;
/// splitting the dialog into per-feature pages requires a small shared
/// surface.
/// </para>
///
/// <para>
/// <see cref="BuildOwnerMap"/> is the single source of truth: it
/// queries the ViewModel for every claimed pin and labels the
/// claimant. Pages call it whenever they need to refresh their
/// combo-box conflict state.
/// </para>
///
/// <para>
/// <see cref="PinAssignmentsChanged"/> is the broadcast: a page raises
/// it after committing a successful pin change; every Hardware page
/// subscribes to refresh its pickers in response. The static event is
/// fine here because the Settings window is single-instance — there is
/// at most one subscriber set at a time, and subscribers detach in
/// their Unloaded handlers.
/// </para>
/// </summary>
internal static class HardwarePins
{
    /// <summary>The GPIO pins exposed to audio-routing UI on the
    /// supported RP2040 / RP2350 boards. The legacy dialog duplicated
    /// this constant in two files; centralising it here removes the
    /// drift risk.</summary>
    public static readonly byte[] ValidPins =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
        13, 14, 15, 16, 17, 18, 19, 20, 21, 22,
        26, 27, 28
    ];

    /// <summary>Whether a GPIO is one of the audio-capable pins
    /// exposed by this board's mux (i.e. is in <see cref="ValidPins"/>).
    /// Used to validate constraints like "BCK pin must have an
    /// audio-capable neighbour at pin+1 to host LRCK".</summary>
    public static bool IsAudioCapable(byte pin) =>
        System.Array.IndexOf(ValidPins, pin) >= 0;

    /// <summary>
    /// GPIO pins that can drive MCK. MCK uses the RP2040/RP2350's
    /// hardware <c>clk_gpout</c> output, which is only wired to a
    /// small subset of GPIOs. The firmware rejects any other pin
    /// with <c>PIN_CONFIG_INVALID_PIN</c> (see
    /// <c>REQ_SET_MCK_PIN</c> in vendor_commands.c).
    /// <list type="bullet">
    /// <item><b>RP2040:</b> GPIO 21 only (23–25 exist on chip but are
    /// board-reserved and not in <see cref="ValidPins"/>).</item>
    /// <item><b>RP2350:</b> GPIO 13, 15, and 21.</item>
    /// </list>
    /// </summary>
    public static byte[] McKCapablePins(string platform) =>
        platform == "RP2350"
            ? new byte[] { 13, 15, 21 }
            : new byte[] { 21 };

    /// <summary>Raised when any Hardware page commits a pin change.
    /// Subscribers (other Hardware pages) call <see cref="BuildOwnerMap"/>
    /// and refresh their own pin combos.</summary>
    public static event Action? PinAssignmentsChanged;

    /// <summary>Notify all pages that the pin map changed. Called by
    /// the originating page after a successful flash write.</summary>
    public static void RaisePinAssignmentsChanged() =>
        PinAssignmentsChanged?.Invoke();

    /// <summary>
    /// Build the current map of pin → owner-label. Used by pin pickers
    /// to grey-out items that another feature already claims.
    /// </summary>
    /// <param name="vm">The application ViewModel; provides every
    /// claimed pin and the labels for each owner.</param>
    /// <param name="excludeOutputId">For output rows: pass the row's
    /// own output id so the picker doesn't grey-out its current pin.
    /// Pass -1 to exclude nothing.</param>
    /// <param name="excludeMckSelf">For the MCK pin combo: true makes
    /// the MCK self-entry omitted so MCK's own current pin stays
    /// selectable; equivalent for SPDIF RX via
    /// <paramref name="excludeSpdifRxSelf"/>.</param>
    /// <param name="excludeSpdifRxSelf">See above.</param>
    /// <param name="excludeDacMuteSelf">See above.</param>
    public static IReadOnlyDictionary<byte, string> BuildOwnerMap(
        MainViewModel vm,
        int excludeOutputId = -1,
        bool excludeMckSelf = false,
        bool excludeSpdifRxSelf = false,
        bool excludeDacMuteSelf = false)
    {
        var owners = new Dictionary<byte, string>();

        // Output GPIO pins. Output id 0..3 map to S/PDIF L of each pair
        // (or I²S DOUT); id 4 (RP2350) / id 2 (RP2040) is PDM. The
        // excludeOutputId guard keeps a row's own pin pickable on its
        // own combo.
        //
        // Use Detail ("OUT 1/2", "SUB OUT", …) rather than Name
        // ("Output 1", "PDM") because every consumer of this map shows
        // the value in a pin-conflict label — and Detail names the
        // actual signal that's on the GPIO, which is what the user
        // needs to know to resolve the conflict. Name is the row
        // header in the Output Assignment editor only.
        var outputs = AllPinOutputs(vm.Platform);
        foreach (var o in outputs)
        {
            if (o.Id == excludeOutputId) continue;
            owners[vm.GetOutputPinValue(o.Id)] = o.Detail;
        }

        // I²S clock pins: BCK reserves both pin and pin+1 (LRCK).
        owners[vm.I2SBckPin] = "BCK";
        owners[(byte)(vm.I2SBckPin + 1)] = "LRCK";

        if (vm.MckEnabled && !excludeMckSelf)
            owners[vm.MckPin] = "MCK";

        if (vm.InputSourceSupported && !excludeSpdifRxSelf)
            owners[vm.SpdifRxPin] = "SPDIF RX";

        // External DAC mute pin (V10+): only claim when supported AND
        // configured with a real pin. The "No Pin" sentinel disables
        // the feature without burning a GPIO.
        if (vm.DacHwMuteSupported
            && !excludeDacMuteSelf
            && vm.DacHwMute.Pin != DacHwMuteConfig.PinNone)
            owners[vm.DacHwMute.Pin] = "DAC Mute";

        return owners;
    }

    /// <summary>Output-row metadata. Each row knows its slot index
    /// (-1 for PDM), its visible name and detail label, the colour
    /// used for the row's dot, and its factory-default pin.</summary>
    public sealed record PinOutput(
        int Id, string Name, string Detail, string Icon,
        byte DefaultPin, Windows.UI.Color Color, int SlotIndex);

    private static readonly PinOutput[] OutputsRp2350 =
    [
        new(0, "Output 1", "OUT 1/2", "", 6,
            Windows.UI.Color.FromArgb(255, 69, 194, 163), 0),
        new(1, "Output 2", "OUT 3/4", "", 7,
            Windows.UI.Color.FromArgb(255, 240, 196, 89), 1),
        new(2, "Output 3", "OUT 5/6", "", 8,
            Windows.UI.Color.FromArgb(255, 89, 140, 242), 2),
        new(3, "Output 4", "OUT 7/8", "", 9,
            Windows.UI.Color.FromArgb(255, 217, 115, 140), 3),
        new(4, "PDM",      "SUB OUT", "", 10,
            Windows.UI.Color.FromArgb(255, 186, 135, 243), -1),
    ];

    private static readonly PinOutput[] OutputsRp2040 =
    [
        new(0, "Output 1", "OUT 1/2", "", 6,
            Windows.UI.Color.FromArgb(255, 69, 194, 163), 0),
        new(1, "Output 2", "OUT 3/4", "", 7,
            Windows.UI.Color.FromArgb(255, 240, 196, 89), 1),
        new(2, "PDM",      "SUB OUT", "", 10,
            Windows.UI.Color.FromArgb(255, 186, 135, 243), -1),
    ];

    /// <summary>Get the platform-correct list of audio output rows.</summary>
    public static IReadOnlyList<PinOutput> AllPinOutputs(string platform) =>
        platform == "RP2350" ? OutputsRp2350 : OutputsRp2040;
}
