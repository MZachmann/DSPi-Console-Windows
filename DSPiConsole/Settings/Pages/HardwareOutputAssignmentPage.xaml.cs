using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › Output Assignment — per-output Type (S/PDIF / I²S / PDM)
/// and GPIO pin, plus a "Reset to defaults" button.
///
/// <para>
/// Pin combos are dynamically built from <see cref="HardwarePins.AllPinOutputs"/>
/// so the page is identical for RP2040 (3 rows) and RP2350 (5 rows).
/// Pin-conflict greying queries <see cref="HardwarePins.BuildOwnerMap"/>
/// and reacts to <see cref="HardwarePins.PinAssignmentsChanged"/> so a
/// pin claim on another Hardware page (I²S, S/PDIF, DAC Mute) repaints
/// this page's pickers without any direct coupling.
/// </para>
///
/// <para>
/// Apply cadence: <b>per-change flash write</b> in Phase 1, matching the
/// legacy dialog. Phase 2 will route through the pending-change tracker.
/// </para>
/// </summary>
public sealed partial class HardwareOutputAssignmentPage : SettingsModule, ISettingsPage
{
    // Per-row tracking. The page rebuilds these from scratch on each
    // Attach so reconnecting to a different platform doesn't leak rows.
    private readonly List<(HardwarePins.PinOutput output, ComboBox typePicker, ComboBox gpioPicker, Border badge)> _rows = new();
    private bool _suppress;

    public HardwareOutputAssignmentPage()
    {
        InitializeComponent();
        // Subscriptions live on Loaded / Unloaded rather than Attach so
        // they survive sidebar-navigation cycles. The SettingsShell
        // caches page instances; switching away from a page detaches
        // it from the visual tree (Unloaded) and switching back
        // re-adds it (Loaded). Attach only runs once — at first
        // navigation — so if we subscribed there, the page would
        // lose event subscriptions on the first sidebar switch and
        // never get them back.
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        // Just store the VM / tracker and run the initial Refresh.
        // Event subscriptions happen in OnPageLoaded which fires
        // right after the shell mounts us in the visual tree.
        base.Attach(vm, tracker);
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Cross-page pin updates: other Hardware pages raise this after
        // a successful flash write so we refresh our conflict labels.
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;

        if (Vm != null)
        {
            // BulkRefreshed covers preset load / factory reset /
            // reconnect — all silently update _outputPins and
            // _outputSlotTypes via FetchAll.
            Vm.BulkRefreshed -= OnBulkRefreshed;
            Vm.BulkRefreshed += OnBulkRefreshed;
            // PropertyChanged catches Platform changes (board swap).
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;

            // Sync from current VM state — covers any events that
            // fired while we were unloaded (sidebar navigated away,
            // then back, after an external preset switch).
            PopulateAfterFetch();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        if (Vm != null)
        {
            Vm.BulkRefreshed -= OnBulkRefreshed;
            Vm.PropertyChanged -= OnVmPropertyChanged;
        }
    }

    private void OnExternalPinChange()
    {
        // Schedule on the dispatcher — event is raised from arbitrary
        // contexts (e.g. inside a USB callback if a page does the work
        // off-thread).
        DispatcherQueue.TryEnqueue(RefreshAllConflicts);
    }

    private void OnBulkRefreshed(object? sender, EventArgs e)
    {
        // The bulk path is already on the UI thread (BulkRefreshed fires
        // from the same dispatcher block), but TryEnqueue is the harmless
        // default for any cross-thread caller. PopulateAfterFetch just
        // mirrors the (now-fresh) VM state into the existing row combos
        // — no row rebuild, no extra device fetches.
        DispatcherQueue.TryEnqueue(PopulateAfterFetch);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Platform changes (first connect after Settings opens, or a
        // board swap from RP2040 to RP2350 / vice versa) change the
        // number of output rows — 3 vs 5. The card layout is decided
        // by HardwarePins.AllPinOutputs(Vm.Platform) inside Refresh,
        // so just call Refresh to rebuild. The follow-up BulkRefreshed
        // event from the same connect flow will populate values.
        if (e.PropertyName == nameof(MainViewModel.Platform))
            DispatcherQueue.TryEnqueue(Refresh);
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        // Wipe and rebuild rows. Cheaper than reconciling differential
        // updates and the page rarely refreshes (only on Attach or a
        // VM-driven bulk refetch).
        OutputRowsHost.Children.Clear();
        _rows.Clear();

        var outputs = HardwarePins.AllPinOutputs(Vm.Platform);

        // Fetch current device state on a background thread, then
        // populate the UI on the dispatcher. Same pattern as the legacy
        // dialog — keeps the USB IO off the UI thread.
        var fetchVm = Vm; // avoid closure on nullable property
        _ = Task.Run(() =>
        {
            foreach (var o in outputs)
                fetchVm.FetchOutputPin(o.Id);
            int slotCount = fetchVm.NumOutputSlots;
            for (int s = 0; s < slotCount; s++)
                fetchVm.FetchOutputSlotType(s);
        }).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PopulateAfterFetch();
            });
        });

        // Build cards immediately with default values; PopulateAfterFetch
        // overwrites them once the device replies. This means the page
        // is interactive even before the fetch completes — the user
        // just sees default pin values briefly.
        //
        // Layout: 2-column grid, one card per output. Cards land at
        // (i/2, i%2). Row definitions are added on demand so a 3-output
        // RP2040 gets 2 rows, a 5-output RP2350 gets 3.
        OutputRowsHost.RowDefinitions.Clear();
        int rowCount = (outputs.Count + 1) / 2;
        for (int r = 0; r < rowCount; r++)
            OutputRowsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < outputs.Count; i++)
        {
            var card = BuildPinCard(outputs[i]);
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            OutputRowsHost.Children.Add(card);
        }

        // Populate the GPIO combos with usable pins for the initial
        // render — without this they'd start empty (BuildPinCard no
        // longer fills them with all pins).
        RefreshAllConflicts();
    }

    private void PopulateAfterFetch()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            foreach (var (output, typePicker, _, badge) in _rows)
            {
                // GPIO combo selection is handled by RefreshAllConflicts
                // below (rebuilds the combo with only usable pins and
                // restores the current selection by Tag).
                byte currentPin = Vm.GetOutputPinValue(output.Id);
                UpdateBadgeVisibility(badge, currentPin, output.DefaultPin);

                // Type (S/PDIF / I²S, or PDM-locked)
                if (output.SlotIndex >= 0)
                {
                    var t = Vm.GetOutputSlotType(output.SlotIndex);
                    typePicker.SelectedIndex = t == OutputSlotType.I2S ? 1 : 0;
                }
            }
        }
        finally { _suppress = false; }
        RefreshAllConflicts();
    }

    /// <summary>
    /// Build one output card: a bordered block with a header row
    /// (colored dot + label, DEFAULT chip right-aligned) and a
    /// controls row (type combo + GPIO combo, each filling half the
    /// card width). Cards are placed by the caller into the 2-column
    /// grid container.
    /// </summary>
    private FrameworkElement BuildPinCard(HardwarePins.PinOutput output)
    {
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 12),
        };

        var content = new Grid { RowSpacing = 10 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // controls

        // ── Header row: dot + label on the left, DEFAULT chip on the right ──
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var idGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        idGroup.Children.Add(new Ellipse
        {
            Width = 10, Height = 10,
            Fill = new SolidColorBrush(output.Color),
            VerticalAlignment = VerticalAlignment.Center,
        });
        idGroup.Children.Add(new TextBlock
        {
            Text = output.Detail,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(idGroup, 0);
        header.Children.Add(idGroup);

        var badge = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "DEFAULT",
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            }
        };
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);

        Grid.SetRow(header, 0);
        content.Children.Add(header);

        // ── Controls row: type combo + GPIO combo, equal width ──
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var typePicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (output.SlotIndex >= 0)
        {
            typePicker.Items.Add(new ComboBoxItem { Content = "S/PDIF", Tag = OutputSlotType.Spdif });
            typePicker.Items.Add(new ComboBoxItem { Content = "I²S",    Tag = OutputSlotType.I2S });
            typePicker.SelectedIndex = 0;
            typePicker.Tag = output;
            typePicker.SelectionChanged += OnOutputTypeChanged;
        }
        else
        {
            typePicker.Items.Add(new ComboBoxItem { Content = "PDM" });
            typePicker.SelectedIndex = 0;
            typePicker.IsEnabled = false;
        }
        Grid.SetColumn(typePicker, 0);
        controls.Children.Add(typePicker);

        // Populate every audio-capable GPIO up front. RefreshAllConflicts
        // later toggles each item's IsEnabled and updates its Content
        // ("GPIO 6 (OUT 3/4)") — it MUST NOT mutate the Items
        // collection, because clearing/rebuilding ComboBox.Items on a
        // dispatcher tick that races a popup dismissal throws "Element
        // not found" (E_FAIL) in WinUI's ComboBox layout. Same fix as
        // the I²S BCK / S/PDIF RX combos.
        var gpioPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = output,
        };
        foreach (var pin in HardwarePins.ValidPins)
            gpioPicker.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        gpioPicker.SelectionChanged += OnGpioChanged;
        Grid.SetColumn(gpioPicker, 1);
        controls.Children.Add(gpioPicker);

        Grid.SetRow(controls, 1);
        content.Children.Add(controls);

        card.Child = content;

        _rows.Add((output, typePicker, gpioPicker, badge));
        return card;
    }

    // ── Live-apply handlers ──────────────────────────────────────────
    // Per-preset parameters — each control change writes through
    // immediately, with revert + status text on error.

    private async void OnOutputTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (sender is not ComboBox combo || combo.Tag is not HardwarePins.PinOutput output) return;
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not OutputSlotType newType) return;

        ClearStatus();

        var status = await Task.Run(() => Vm.SetOutputSlotType(output.SlotIndex, newType));

        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshAllConflicts();
            // Slot-type changes are slot-scoped (one slot drives one
            // S/PDIF stereo pair or one I²S link), so the status message
            // names the slot, not the output. Pin changes below still
            // talk about the output (which GPIO it lives on).
            ShowStatus($"Slot {output.SlotIndex + 1} → {(newType == OutputSlotType.I2S ? "I²S" : "S/PDIF")}", isError: false);
            return;
        }

        // Revert UI to whatever the device actually reports.
        _suppress = true;
        combo.SelectedIndex = Vm.GetOutputSlotType(output.SlotIndex) == OutputSlotType.I2S ? 1 : 0;
        _suppress = false;
        ShowStatus(I2SError(status, output.Name), isError: true);
    }

    private async void OnGpioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (sender is not ComboBox combo || combo.Tag is not HardwarePins.PinOutput output) return;
        if (combo.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not byte newPin) return;

        ClearStatus();

        var status = await Task.Run(() => Vm.SetOutputPinValue(output.Id, newPin));

        if (status == PinConfigResult.Success)
        {
            UpdateRowBadge(output, newPin);
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshAllConflicts();
            ShowStatus($"{output.Name} → GPIO {newPin}", isError: false);
            return;
        }

        // PDM has the special auto-cycle (disable, set pin, re-enable).
        // Firmware refuses to move the PDM pin while the PDM output
        // is enabled. Mirrors the legacy dialog's branch for
        // output.SlotIndex < 0.
        if (status == PinConfigResult.OutputActive && output.SlotIndex < 0)
        {
            var cycleStatus = await Task.Run(() =>
            {
                int pdmMatrixIndex = Vm.ActiveOutputs.Count - 1;
                Vm.Device.SetOutputEnable(pdmMatrixIndex, false);
                var r = Vm.SetOutputPinValue(output.Id, newPin);
                Vm.Device.SetOutputEnable(pdmMatrixIndex, true);
                return r;
            });

            if (cycleStatus == PinConfigResult.Success)
            {
                UpdateRowBadge(output, newPin);
                HardwarePins.RaisePinAssignmentsChanged();
                RefreshAllConflicts();
                ShowStatus($"{output.Name} → GPIO {newPin}", isError: false);
                return;
            }
            status = cycleStatus;
        }

        RevertGpioCombo(output);
        ShowStatus(GpioError(status, output.Name), isError: true);
    }

    private void UpdateRowBadge(HardwarePins.PinOutput output, byte newPin)
    {
        var row = _rows.FirstOrDefault(r => r.output.Id == output.Id);
        if (row != default)
            UpdateBadgeVisibility(row.badge, newPin, output.DefaultPin);
    }

    private void RevertGpioCombo(HardwarePins.PinOutput output)
    {
        if (Vm == null) return;
        var row = _rows.FirstOrDefault(r => r.output.Id == output.Id);
        if (row == default) return;
        SelectPinInCombo(row.gpioPicker, Vm.GetOutputPinValue(output.Id));
    }

    private static void UpdateBadgeVisibility(Border badge, byte currentPin, byte defaultPin) =>
        badge.Visibility = currentPin == defaultPin ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Refresh per-item state on every GPIO picker so pins
    /// claimed by other features appear disabled and labelled with
    /// their owner ("GPIO 6 (OUT 3/4)"), while still-selectable pins
    /// read as plain "GPIO N". The Items collection itself is never
    /// modified here — that's a hard requirement because WinUI's
    /// ComboBox throws "Element not found" (E_FAIL) when its Items
    /// are cleared/rebuilt on a dispatcher tick that races the
    /// popup-dismissal of a user selection. Items are populated once
    /// in BuildPinCard.</summary>
    private void RefreshAllConflicts()
    {
        if (Vm == null) return;
        foreach (var (output, _, picker, _) in _rows)
            RefreshGpioCombo(output, picker);
    }

    /// <summary>
    /// Update one row's GPIO picker so each item is correctly enabled
    /// and labelled against the current owner map. Selection is
    /// restored by matching the Tag byte. Does NOT touch the Items
    /// collection — see RefreshAllConflicts for the rationale.
    /// </summary>
    private void RefreshGpioCombo(HardwarePins.PinOutput targetOutput, ComboBox picker)
    {
        if (Vm == null) return;
        var owners = HardwarePins.BuildOwnerMap(Vm, excludeOutputId: targetOutput.Id);
        byte currentPin = Vm.GetOutputPinValue(targetOutput.Id);

        _suppress = true;
        try
        {
            for (int i = 0; i < picker.Items.Count; i++)
            {
                if (picker.Items[i] is not ComboBoxItem item) continue;
                if (item.Tag is not byte pin) continue;

                bool isCurrent = pin == currentPin;
                string? ownerLabel = null;
                if (!isCurrent && owners.TryGetValue(pin, out var owner))
                    ownerLabel = owner;

                item.Content = ownerLabel != null
                    ? $"GPIO {pin} ({ownerLabel})"
                    : $"GPIO {pin}";
                item.IsEnabled = ownerLabel == null;
            }
            SelectPinInCombo(picker, currentPin);
        }
        finally { _suppress = false; }
    }

    /// <summary>Select the item whose Tag is the given pin byte. No-op
    /// if no item matches — leaves the previous selection in place
    /// rather than blanking the combo.</summary>
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

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        ClearStatus();

        // Per-preset: reset writes through immediately, in the firmware's
        // required order. Slot types must go to S/PDIF before BCK pin can
        // move; MCK must be off before its pin can move.

        // Reset slot types → S/PDIF.
        int slotCount = Vm.NumOutputSlots;
        for (int s = 0; s < slotCount; s++)
        {
            if (Vm.GetOutputSlotType(s) != OutputSlotType.Spdif)
            {
                var typeStatus = await Task.Run(() => Vm.SetOutputSlotType(s, OutputSlotType.Spdif));
                if (typeStatus != PinConfigResult.Success)
                {
                    ShowStatus($"Failed to reset Output {s + 1} type", isError: true);
                    return;
                }
            }
        }

        // Reset GPIO pins.
        foreach (var (output, typePicker, gpioPicker, badge) in _rows)
        {
            byte defaultPin = output.DefaultPin;
            byte currentPin = Vm.GetOutputPinValue(output.Id);
            if (currentPin == defaultPin) continue;

            byte status;
            if (output.SlotIndex < 0) // PDM auto-cycle
            {
                status = await Task.Run(() =>
                {
                    int pdmMatrixIndex = Vm.ActiveOutputs.Count - 1;
                    Vm.Device.SetOutputEnable(pdmMatrixIndex, false);
                    var r = Vm.SetOutputPinValue(output.Id, defaultPin);
                    Vm.Device.SetOutputEnable(pdmMatrixIndex, true);
                    return r;
                });
            }
            else
            {
                status = await Task.Run(() => Vm.SetOutputPinValue(output.Id, defaultPin));
            }

            if (status == PinConfigResult.Success)
            {
                _suppress = true;
                var idx = System.Array.IndexOf(HardwarePins.ValidPins, defaultPin);
                if (idx >= 0) gpioPicker.SelectedIndex = idx;
                UpdateBadgeVisibility(badge, defaultPin, output.DefaultPin);
                _suppress = false;
            }
            else
            {
                ShowStatus($"Failed to reset {output.Name}: {GpioError(status, output.Name)}", isError: true);
                return;
            }
        }

        // Reset I²S clocks to defaults (MCK off, BCK 14, MCK pin 13, mult 128).
        await Task.Run(() =>
        {
            Vm.SetMckEnable(false);
            Vm.SetI2SBckPin(14);
            Vm.SetMckPin(13);
            Vm.SetMckMultiplier(128);
        });

        // Re-sync UI: type combos back to S/PDIF.
        _suppress = true;
        foreach (var (output, typePicker, _, _) in _rows)
        {
            if (output.SlotIndex >= 0)
                typePicker.SelectedIndex = 0;
        }
        _suppress = false;

        HardwarePins.RaisePinAssignmentsChanged();
        RefreshAllConflicts();
        ShowStatus("All hardware settings reset to defaults.", isError: false);
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

    private static string GpioError(byte status, string outputName) => status switch
    {
        PinConfigResult.InvalidPin    => "Invalid GPIO pin number",
        PinConfigResult.PinInUse      => "Pin is already assigned to another output",
        PinConfigResult.InvalidOutput => "Invalid output index",
        PinConfigResult.OutputActive  => $"{outputName} must be disabled before changing its pin",
        0xFF                          => "USB transfer failed — device may be disconnected",
        _                             => $"Unknown error (0x{status:X2})"
    };

    private static string I2SError(byte status, string contextName) => status switch
    {
        PinConfigResult.InvalidPin    => "Invalid GPIO pin number",
        PinConfigResult.PinInUse      => "Pin is already assigned to another function",
        PinConfigResult.InvalidOutput => "Invalid output slot index",
        PinConfigResult.OutputActive  => "Cannot change while I²S outputs are active",
        0xFF                          => "USB transfer failed — device may be disconnected",
        _                             => $"Unknown error (0x{status:X2})"
    };

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "hardware.output-assignment";
    public string Title => "Output Assignment";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => ""; // Speakers
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareOutputAssignmentPage();
        p.Attach(vm, tracker);
        return p;
    }
}
