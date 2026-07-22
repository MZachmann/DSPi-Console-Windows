using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Usb;

/// <summary>
/// Handles low-level USB communication for the DSPi device.
/// </summary>
public partial class DspiUsb : ObservableObject, IDspiTransfer
{
    // Device identification
    public const int VendorId = 0x2E8B;
    public const int ProductId = 0xFEAA;

    // Interface 2 is the vendor-specific control interface
    public const int VendorInterfaceNumber = 2;

    // USB Request Types
    // 0x41 = 01000001 (Dir: Host-to-Device | Type: Vendor | Recipient: Interface)
    // 0xC1 = 11000001 (Dir: Device-to-Host | Type: Vendor | Recipient: Interface)
    private const byte RequestTypeOut = 0x41;
    private const byte RequestTypeIn = 0xC1;

    private readonly UsbContext _context = new();
    private IUsbDevice? _device;
    private bool _interfaceClaimed;
    private byte _openBusNumber;
    private byte _openAddress;
    private readonly object _lock = new();
    private readonly System.Timers.Timer _pollTimer;
    private readonly System.Timers.Timer _statusPollTimer;
    private bool _disposed;

    private List<DSPiDeviceInfo> _availableDevices = new();
    
    [ObservableProperty]
    private DSPiDeviceInfo? _selectedDeviceInfo;
    
    private string? _lastSelectedSerial;
    
    [ObservableProperty]
    private string? _openDeviceSerial; // serial of the currently open _device handle

    // Notification endpoint state
    private UsbEndpointReader? _notifyReader;
    private Thread? _notifyThread;
    private volatile bool _notifyStop;
    private const int NotifyPacketSize = 64;

    public string DeviceType => "USB";
    
    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private SystemStatus? _currentStatus;
    
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevices => _availableDevices;

    public event EventHandler<NotifyPacket>? NotifyPacketReceived;
    public event EventHandler? AvailableDevicesChanged;
    public event EventHandler? DeviceConnected;
    public event EventHandler? DeviceDisconnected;
    public event EventHandler? StatusPollRequested;

    public DspiUsb()
    {
        // Poll for devices every 500ms
        _pollTimer = new System.Timers.Timer(500);
        _pollTimer.Elapsed += (_, _) => ScanDevices();
        _pollTimer.AutoReset = true;

        // Poll for status every 100ms when connected
        _statusPollTimer = new System.Timers.Timer(100);
        _statusPollTimer.Elapsed += (_, _) => StatusPollRequested?.Invoke(this, EventArgs.Empty);
        _statusPollTimer.AutoReset = true;
    }

    public void StartMonitoring()
    {
        _pollTimer.Start();
        ScanDevices();
    }

    public void StopMonitoring()
    {
        _pollTimer.Stop();
    }

    /// <summary>
    /// Scan for all connected DSPi devices, update the available list,
    /// and auto-select/reconnect as needed.
    /// </summary>
    public void ScanDevices()
    {
        if (_disposed) return;

        try
        {
            using var allDevicesList = _context.List();
            var matching = allDevicesList
                .Where(d => d.VendorId == VendorId && d.ProductId == ProductId)
                .ToList();

            // Drop cache entries for devices that have unplugged.
            var liveKeys = matching.Select(GetBusAddr).ToHashSet();
            foreach (var stale in _serialCache.Keys.Where(k => !liveKeys.Contains(k)).ToList())
                _serialCache.Remove(stale);

            // Build the current device list. For the device we already have open,
            // skip the open/claim/read cycle entirely — we know its serial.
            var currentDevices = new List<DSPiDeviceInfo>();
            (byte bus, byte addr) openKey = (_openBusNumber, _openAddress);
            bool weHaveOpen = _device != null && IsConnected && SelectedDeviceInfo != null;

            foreach (var dev in matching)
            {
                var key = GetBusAddr(dev);

                if (weHaveOpen && key.bus == openKey.bus && key.addr == openKey.addr)
                {
                    currentDevices.Add(SelectedDeviceInfo!);
                    continue;
                }

                if (_serialCache.TryGetValue(key, out var cachedSerial))
                {
                    currentDevices.Add(new DSPiDeviceInfo(cachedSerial,
                        $"vid_{VendorId:X4}&pid_{ProductId:X4}#{cachedSerial}"));
                    continue;
                }

                // Unknown device — open briefly to read serial, then close.
                try
                {
                    if (!dev.TryOpen()) continue;
                    try
                    {
                        if (!ConfigureAndClaim(dev)) continue;
                        var serial = ReadSerialFromDevice(dev);
                        if (!string.IsNullOrEmpty(serial))
                        {
                            _serialCache[key] = serial!;
                            currentDevices.Add(new DSPiDeviceInfo(serial!,
                                $"vid_{VendorId:X4}&pid_{ProductId:X4}#{serial}"));
                        }
                    }
                    finally
                    {
                        try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                        try { dev.Close(); } catch { }
                    }
                }
                catch { }
            }

            var oldSerials = _availableDevices.Select(d => d.Serial).ToHashSet();
            var newSerials = currentDevices.Select(d => d.Serial).ToHashSet();
            if (!oldSerials.SetEquals(newSerials))
            {
                _availableDevices = currentDevices;
                AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
            }

            if (SelectedDeviceInfo != null && !newSerials.Contains(SelectedDeviceInfo.Serial))
            {
                lock (_lock) { HandleDisconnect(); }
                if (currentDevices.Count == 0) SelectedDeviceInfo = null;
            }

            if (_device != null && IsConnected)
            {
                if (matching.Count == 0)
                    lock (_lock) { HandleDisconnect(); }
                return;
            }

            if (_device == null && currentDevices.Count > 0)
            {
                var reconnectTarget = _lastSelectedSerial != null
                    ? currentDevices.FirstOrDefault(d => d.Serial == _lastSelectedSerial)
                    : null;
                OpenDevice(reconnectTarget ?? currentDevices[0]);
            }
            else if (currentDevices.Count == 0)
            {
                if (ErrorMessage == null || ErrorMessage == "Disconnected")
                {
                    using var anyDevicesList = _context.List();
                    if (anyDevicesList.Count == 0)
                        ErrorMessage = "No USB devices visible to libusb-1.0. Install the WinUSB driver for the DSPi vendor interface.";
                    else
                        ErrorMessage = "Disconnected";
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScanDevices error: {ex.Message}");
        }
    }

    public void OpenDevice(DSPiDeviceInfo deviceInfo)
    {
        lock (_lock)
        {
            try
            {
                if (IsConnected)
                {
                    Close();
                }

                List<IUsbDevice> candidates = new();
                foreach (var d in ListMatchingDevices())
                {
                    candidates.Add(d.Clone());
                }

                IUsbDevice? opened = null;
                foreach (var dev in candidates)
                {
                    if (opened != null)
                    {
                        try { dev.Close(); } catch { }
                        continue;
                    }

                    if (!dev.TryOpen())
                    {
                        try { dev.Close(); } catch { }
                        continue;
                    }

                    bool claimed = false;
                    try
                    {
                        if (!ConfigureAndClaim(dev))
                        {
                            try { dev.Close(); } catch { }
                            continue;
                        }
                        claimed = true;

                        var serial = ReadSerialFromDevice(dev);
                        if (serial == deviceInfo.Serial)
                        {
                            opened = dev;
                        }
                        else
                        {
                            try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                            try { dev.Close(); } catch { }
                        }
                    }
                    catch
                    {
                        if (claimed)
                            try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                        try { dev.Close(); } catch { }
                    }
                }

                if (opened == null)
                {
                    return;
                }

                if (!Open(opened, deviceInfo.Serial))
                {
                    return;
                }

                SelectedDeviceInfo = deviceInfo;
                _lastSelectedSerial = deviceInfo.Serial;
                
                _statusPollTimer.Start();
                DeviceConnected?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenDevice error: {ex.Message}");
                Close();
            }
        }
    }

    public IEnumerable<IUsbDevice> ListMatchingDevices()
    {
        return _context.List().Where(d => d.VendorId == VendorId && d.ProductId == ProductId);
    }

    /// <summary>
    /// Cached serials keyed by (bus, address). Avoids opening matching devices
    /// repeatedly on every scan tick — a duplicate open of the currently-active
    /// device disturbs the in-flight control transfer queue on the original
    /// handle (WinUSB lets the second open through, then SetConfiguration /
    /// ClaimInterface on the duplicate handle stomps the original's state and
    /// every subsequent control GET silently returns 0 bytes).
    /// </summary>
    private readonly Dictionary<(byte bus, byte addr), string> _serialCache = new();

    private static (byte bus, byte addr) GetBusAddr(IUsbDevice dev)
    {
        // BusNumber/Address live on the concrete UsbDevice — IUsbDevice doesn't
        // expose them. Fall back to (0,0) if the cast fails (won't happen with
        // the libusb-1.0 backend, which always returns UsbDevice instances).
        if (dev is UsbDevice ud) 
            return (ud.BusNumber, ud.Address);
        return (0, 0);
    }

    /// <summary>
    /// Configure (idempotent on Windows/WinUSB) and claim the vendor interface
    /// on a freshly opened device. Returns true on success.
    /// </summary>
    private static bool ConfigureAndClaim(IUsbDevice dev)
    {
        try { dev.SetConfiguration(1); }
        catch { /* already configured by Windows; libusb winusb backend tolerates this */ }
        return dev.ClaimInterface(VendorInterfaceNumber);
    }

    /// <summary>
    /// Read the serial number from a temporarily opened USB device using the
    /// vendor GET_SERIAL control request. The device must already be open and
    /// have its interface claimed.
    /// </summary>
    private static string? ReadSerialFromDevice(IUsbDevice tempDevice)
    {
        var setupPacket = new UsbSetupPacket(RequestTypeIn, VendorCommands.GetSerial, 0, VendorInterfaceNumber, 16);
        var buffer = new byte[16];
        int transferred;
        try
        {
            transferred = tempDevice.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
        }
        catch
        {
            // Hot-plug races and brief WinUSB stalls can throw here during
            // enumeration / open-by-serial. Treat them as "no serial" so the
            // poll loop just skips this device and retries on the next tick.
            return null;
        }
        if (transferred > 0)
            return System.Text.Encoding.ASCII.GetString(buffer, 0, transferred).TrimEnd('\0');
        return null;
    }


    public bool Open(IUsbDevice dev, string serial)
    {
        lock (_lock)
        {
            Close();
            _device = dev;
            var key = GetBusAddr(dev);
            _openBusNumber = key.bus;
            _openAddress = key.addr;
            _interfaceClaimed = true; // Assumed claimed before calling Open or by Open logic
            
            StartNotifyListener(_device);
            return true;
        }
    }

    private void HandleDisconnect()
    {
        _statusPollTimer.Stop();
        StopNotifyListener();

        var wasConnected = IsConnected;

        if (_device != null)
        {
            if (_interfaceClaimed)
                try { _device.ReleaseInterface(VendorInterfaceNumber); } catch { }
            try { _device.Close(); } catch { }
        }
        _device = null;
        _interfaceClaimed = false;
        _openBusNumber = 0;
        _openAddress = 0;
        IsConnected = false;

        if (wasConnected)
        {
            ErrorMessage = "Disconnected";
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
    }

    public void Reconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
        ScanDevices();
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_device == null) return;
            StopNotifyListener();
            _statusPollTimer.Stop();
            if (_interfaceClaimed)
            {
                try { _device.ReleaseInterface(VendorInterfaceNumber); } catch { }
            }
            try { _device.Close(); } catch { }
            _device = null;
            _interfaceClaimed = false;
            _openBusNumber = 0;
            _openAddress = 0;
            SelectedDeviceInfo = null;
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool ControlTransferOut(byte request, ushort value = 0, byte[]? data = null)
    {
        lock (_lock)
        {
            if (_device == null) return false;

            var buffer = data ?? Array.Empty<byte>();
            var setupPacket = new UsbSetupPacket(
                RequestTypeOut,
                request,
                value,
                VendorInterfaceNumber,
                buffer.Length);

            try
            {
                int transferred = _device.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
                return transferred >= 0;
            }
            catch
            {
                // libusb / LibUsbDotNet throws on stall, device disappearance
                // mid-transfer, NAK timeout, etc. Surface those as "transfer
                // failed" rather than unwinding through an async void handler
                // and killing the process. Callers already treat false as a
                // generic USB failure.
                return false;
            }
        }
    }

    public byte[]? ControlTransferIn(byte request, ushort value = 0, int length = 4)
    {
        lock (_lock)
        {
            if (_device == null) return null;

            var setupPacket = new UsbSetupPacket(
                RequestTypeIn,
                request,
                value,
                VendorInterfaceNumber,
                length);

            var buffer = new byte[length];
            int transferred;
            try
            {
                transferred = _device.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
            }
            catch
            {
                // See ControlTransferOut for the rationale — keep USB stack
                // exceptions from propagating through async void handlers.
                return null;
            }

            if (transferred > 0)
            {
                if (transferred < length)
                {
                    var result = new byte[transferred];
                    Array.Copy(buffer, result, transferred);
                    return result;
                }
                return buffer;
            }

            return null;
        }
    }

    /// <summary>
    /// Open EP 0x83 (bulk IN, 64-byte packets) and start a background reader
    /// that decodes v2 PARAM_CHANGED / BULK_INVALIDATED / PRESET_LOADED events.
    /// The firmware always keeps this endpoint armed with a 1-byte IDLE
    /// keep-alive when nothing is pending, so reads return promptly.
    /// </summary>
    private void StartNotifyListener(IUsbDevice dev)
    {
        try
        {
            _notifyReader = dev.OpenEndpointReader(ReadEndpointID.Ep03, NotifyPacketSize, EndpointType.Bulk);
        }
        catch
        {
            _notifyReader = null;
            return;
        }

        _notifyStop = false;
        _notifyThread = new Thread(NotifyReadLoop)
        {
            IsBackground = true,
            Name = "DSPi notify"
        };
        _notifyThread.Start();
    }

    private void StopNotifyListener()
    {
        _notifyStop = true;
        // Closing the device handle (in HandleDisconnect, right after this call)
        // will cause any pending Read() to error out and the loop to exit. We
        // join with a short timeout so a stuck thread doesn't block disconnect.
        var thread = _notifyThread;
        _notifyThread = null;
        _notifyReader = null;
        if (thread != null && thread.IsAlive)
        {
            try { thread.Join(500); } catch { }
        }
    }
   
    private void NotifyReadLoop()
    {
        var buf = new byte[NotifyPacketSize];
        while (!_notifyStop)
        {
            UsbEndpointReader? reader = _notifyReader;
            if (reader == null) break;

            int len;
            LibUsbDotNet.Error err;
            try
            {
                err = reader.Read(buf, 1000, out len);
            }
            catch
            {
                // Device closed underneath us; exit cleanly.
                break;
            }

            if (_notifyStop) break;

            if (err == LibUsbDotNet.Error.Timeout || err == LibUsbDotNet.Error.Interrupted)
                continue;
            if (err == LibUsbDotNet.Error.NoDevice || err == LibUsbDotNet.Error.NotFound)
                break;
            if (err != LibUsbDotNet.Error.Success || len <= 0)
                continue;

            // Fire the raw-packet event before decoding so the Bulk Endpoint
            // Monitor sees IDLE keep-alives, unknown event IDs, and malformed
            // packets too — not just the subset ProcessNotifyPacket understands.
            // Copy the slice we care about; the next read overwrites buf.
            var rawListeners = NotifyPacketReceived;
            if (rawListeners != null)
            {
                var copy = new byte[len];
                Buffer.BlockCopy(buf, 0, copy, 0, len);
                try { rawListeners(this, new NotifyPacket { Data = copy, Timestamp = DateTime.Now }); }
                catch { /* a misbehaving subscriber must not break the reader */ }
            }

            // try { ProcessNotifyPacket(buf, len); }
            // catch { /* a malformed packet is not fatal */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Dispose();
        _statusPollTimer.Stop();
        _statusPollTimer.Dispose();
        Disconnect();
        _context.Dispose();

        GC.SuppressFinalize(this);
    }

}
