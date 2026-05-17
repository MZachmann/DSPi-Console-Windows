using System.ComponentModel;
using System.Threading.Tasks;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › S/PDIF Input — single RX pin combo. Only registered in
/// the sidebar when the connected firmware supports input-source
/// switching (V7+).
/// </summary>
public sealed partial class HardwareSpdifInputPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public HardwareSpdifInputPage()
    {
        InitializeComponent();
        // Combo items are added by RefreshConflicts — filter on
        // populate (only show usable pins; pins claimed elsewhere
        // are omitted, not greyed out). Initial empty state is fine
        // because Refresh() runs before the page is shown.

        // Subscriptions in Loaded/Unloaded so they survive sidebar
        // navigation cycles (see HardwareOutputAssignmentPage for why).
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);

        // Fetch from device on a background thread. Both calls share the
        // same continuation — the page redraws once when either lands.
        var fetchVm = vm;
        _ = Task.Run(() =>
        {
            fetchVm.FetchSpdifRxPin();
            fetchVm.FetchLgSoundSync();
        }).ContinueWith(_ => DispatcherQueue.TryEnqueue(Refresh));
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;
        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;
            // Re-sync from VM state in case events were missed while
            // we were unloaded.
            Refresh();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnExternalPinChange() =>
        DispatcherQueue.TryEnqueue(RefreshConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SpdifRxPin can change externally on preset load / reconnect.
        // LgSoundSyncEnabled / LgSoundSyncSupported track the same path —
        // preset load may flip the enable bit, and a reconnect to older
        // firmware drops the supported flag. Refresh on any of them.
        if (e.PropertyName == nameof(MainViewModel.SpdifRxPin)
            || e.PropertyName == nameof(MainViewModel.LgSoundSyncEnabled)
            || e.PropertyName == nameof(MainViewModel.LgSoundSyncSupported))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;
        // Combo contents + selection are both handled by
        // RefreshConflicts — it rebuilds with only usable pins, then
        // selects the device's current RX pin by Tag.
        RefreshConflicts();
        RefreshLgSoundSync();
    }

    private void RefreshLgSoundSync()
    {
        if (Vm == null) return;
        // Hide the entire card on firmware that doesn't expose the feature
        // (pre-V8 STALLs REQ_GET_LG_SOUND_SYNC_ENABLE so the probe drops
        // LgSoundSyncSupported to false). Suppress the Toggled handler
        // while we re-sync from VM state so the local update doesn't
        // ricochet back through Vm.LgSoundSyncEnabled and trigger a
        // redundant USB write.
        LgSoundSyncCard.Visibility = Vm.LgSoundSyncSupported
            ? Visibility.Visible : Visibility.Collapsed;
        _suppress = true;
        try { LgSoundSyncToggle.IsOn = Vm.LgSoundSyncEnabled; }
        finally { _suppress = false; }
    }

    private void OnLgSoundSyncToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        // Live apply — per-preset parameter, writes through immediately
        // to RAM via REQ_SET_LG_SOUND_SYNC_ENABLE (0xE6). The VM setter
        // dispatches the USB control transfer on a background task.
        Vm.LgSoundSyncEnabled = LgSoundSyncToggle.IsOn;
    }

    /// <summary>Rebuild the RX pin combo so it lists only pins this
    /// picker can actually use — the current RX pin (always selectable
    /// so the user can re-confirm) plus any audio-capable GPIO not
    /// claimed by another feature.</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;

        var owners = HardwarePins.BuildOwnerMap(Vm, excludeSpdifRxSelf: true);
        byte currentPin = Vm.SpdifRxPin;

        _suppress = true;
        try
        {
            SpdifRxPinCombo.Items.Clear();
            foreach (var pin in HardwarePins.ValidPins)
            {
                if (pin == currentPin || !owners.ContainsKey(pin))
                    SpdifRxPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
            }
            SelectPinInCombo(SpdifRxPinCombo, currentPin);
        }
        finally { _suppress = false; }
    }

    /// <summary>Select the item whose byte Tag matches <paramref name="pin"/>.
    /// No-op if no match — leaves the previous selection in place.</summary>
    private static void SelectPinInCombo(ComboBox combo, byte pin)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag is byte p && p == pin)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private async void OnSpdifRxPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (SpdifRxPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();

        // Live apply — per-preset parameter, writes through immediately
        // to RAM. The firmware call still travels over USB so we Task.Run
        // it to keep the UI responsive; status feedback surfaces inline.
        var status = await Task.Run(() => Vm.SetSpdifRxPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshConflicts();
            ShowStatus($"S/PDIF RX pin set to GPIO {newPin}", false);
            return;
        }

        // Revert combo to device's actual value on failure. Combo
        // contents are filter-on-populate so we can't index by
        // ValidPins — match by Tag instead.
        _suppress = true;
        SelectPinInCombo(SpdifRxPinCombo, Vm.SpdifRxPin);
        _suppress = false;

        var msg = status switch
        {
            PinConfigResult.PinInUse => $"GPIO {newPin} is already in use",
            _ => $"Failed to set RX pin (0x{status:X2})"
        };
        ShowStatus(msg, true);
    }

    private void ShowStatus(string msg, bool isError)
    {
        StatusText.Text = msg;
        StatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 240, 100, 100)
            : Color.FromArgb(255, 100, 200, 140));
        StatusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus() => StatusText.Visibility = Visibility.Collapsed;

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "hardware.spdif-input";
    public string Title => "S/PDIF Input";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => ""; // OpenWith / input
    public int Order => 30;
    // V7+ feature — hide the sidebar entry entirely on older firmware.
    public bool IsAvailable(MainViewModel vm) => vm.InputSourceSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareSpdifInputPage();
        p.Attach(vm, tracker);
        return p;
    }
}
