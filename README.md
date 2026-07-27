# DSPi Console for Windows -- Remote Fork

This fork of the Weeb labs DSPi project Windows Console allows the <span style="color: #2ECC71;">**console to attach to a remote DSPi**</span>
using the multi-platform tiny DSPiCliServer application. You can control a remote DSPi Pico anywhere (Windows, Mac, Linux) using the Windows gui.

The common usage is to have a remote slow & tiny Linux CPU or old Mac or Windows machine connected to the DSPi Pico USB. Then run DSPiCliServer on the remote machine,  
which lets you then 

1. run a web browser (from anything on your network like a phone) and browse to the server to control the DSPi volumes, loudness, preset and other settings
2. use this DSPiConsole application to set up and control the DSPi remotely from any Windows computer on your network

## Usage
Run this like the usual Windows DSPiConsole application but with 1 or 2 optional arguments:

` DSPiConsole [-r myremoteip] [-p myremoteport]`

If an -r argument is provided, the console will attempt to connect to the specified remote DSPiCliRemote instance
and use it as the DSPi Pico. If no -r argument is provided, the console will connect to your USB-local DSPi (as usual).

Ensure that DSPiCliServer is running on the remote device. Go to the [DSPiCliRemote](https://github.com/MZachmann/DSPiCliRemote) 
repository and get the latest release then follow the installation instructions. I usually run it by just running a
console (terminal) and running it manually. For more permanent installations I set DSPiCliServer to run at system startup.

Once DSPiCliServer is running, you can swap to your Windows PC.

Examples :

```
DSPiConsole -r pizero.local # connect to the DSPi at pizero.local (port 8082)
DSPiConsole # connect to the DSPi on USB
DSPiConsole -r pizero.local -p 8086 # connect the DSPi at pizero.local using CLI port 8086
```

## ====================================================================

### DSPi Console for Windows

A native WinUI 3 control application for the [DSPi audio processor](https://github.com/WeebLabs/DSPi), open source
DSP Firmware that turns a Raspberry Pi Pico (RP2040) or Pico 2 (RP2350) into a capable multi-output USB audio
interface with an onboard signal processor.

DSPi Console provides complete control over the device: parametric equalisation, active crossovers, routing, time
alignment, loudness compensation, headphone crossfeed, dynamics processing, bass enhancement, stereo upmixing,
physical control surfaces and hardware configuration, all applied live over USB and requiring no reflashing.

ScreenShot... (elided)

---

## Contents

- [Important: match Console and Firmware versions](#important-match-console-and-firmware-versions)
- [Getting started](#getting-started)
- [The main window at a glance](#the-main-window-at-a-glance)
- [Feature reference](#feature-reference)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Settings](#settings)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)
- [Project structure](#project-structure)
- [Related projects](#related-projects)
- [License and acknowledgements](#license-and-acknowledgements)

---

## Important: match Console and Firmware versions

**Run Console and Firmware at exactly the same version, including the same beta or hotfix suffix, unless a
particular release explicitly states otherwise.**

Console and Firmware share a private USB control protocol that evolves with each release. New parameters,
new wire layouts and new bulk-transfer sections are introduced together on both sides. Mixing versions is not a
supported configuration, and the consequences range from the merely confusing to the potentially damaging:

- Features silently disappear from the interface because the device does not answer the capability probe for them.
- Values are written to the wrong field, so a control that should set a frequency may set a gain instead.
- Bulk configuration reads are misparsed, which can leave the interface misrepresenting the state of the device.

Every release of DSPi Console names the Firmware version against which it is built, and every Firmware release
names the Console version that accompanies it. Update both together, and consult the release notes beforehand: if
a release is compatible with a wider range of versions, it will say so.

Console degrades gracefully where it can. It probes the device for each capability at connection time and hides
the controls the connected device's Firmware cannot support, rather than issuing commands the device would reject.
This behaviour is a mitigation, not a substitute for matched versions.

Firmware releases are published in the [DSPi Firmware repository](https://github.com/WeebLabs/DSPi/releases), and
Console releases on [this repository's releases page](https://github.com/WeebLabs/DSPi-Console-Windows/releases).

---

## Getting started

### 1. Requirements

**Hardware**

- A Raspberry Pi Pico (RP2040) or Pico 2 (RP2350) running DSPi Firmware, together with the DACs, amplifiers and
  optical receivers appropriate to your installation. The
  [Firmware repository](https://github.com/WeebLabs/DSPi) documents the wiring, the default GPIO assignments and
  the signal chain in detail.
- A USB cable that carries data. Charge-only cables will not enumerate the device.

**Software**

- Windows 10 version 1809 (build 17763) or later, 64-bit.
- The [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). The Windows App SDK is bundled
  with the application, so this is the only prerequisite you need to install yourself.

### 2. Install Firmware

If your device is new, or if you are updating to match a new Console release:

1. Download the `.uf2` Firmware image for your board from the
   [DSPi releases page](https://github.com/WeebLabs/DSPi/releases). RP2040 and RP2350 builds are separate files;
   ensure you select the one that matches your board.
2. Put the board into bootloader mode. On a new board, hold the BOOTSEL button while connecting it to USB. If
   DSPi is already running and Console can see the device, use **File > Update Firmware**, which reboots it into
   the bootloader without requiring physical access to the button.
3. The board appears as a removable drive named `RPI-RP2` or similar. Copy the `.uf2` file onto it. The board
   reboots automatically once the copy completes.

### 3. Install Console

1. Download the latest `DSPi.Console.v<version>.zip` from the
   [releases page](https://github.com/WeebLabs/DSPi-Console-Windows/releases), choosing the release that matches
   your Firmware version.
2. Extract the archive to a location of your choosing. The application is portable and requires no installer.
3. Run `DSPiConsole.exe`.

### 4. Connect

Connect the device and launch Console. Detection is automatic: the title bar shows the connected device, and
the sidebar populates with the input and output channels your platform provides. If more than one DSPi device is
attached, a selection dialog appears.

Windows also presents the device as a standard USB audio interface. Select it as your playback device in the
Windows sound settings, and choose the desired format in the device's advanced properties. The number of input
channels Console displays follows the format Windows is streaming, so an eight-channel format yields eight
independently processed input channels.

### 5. Your first adjustments

**Equalise a channel.** Click a channel in the sidebar to open its editor. Each channel provides ten parametric
bands. Choose a filter type from the dropdown, then set the frequency, Q and gain. Values accept typed entry,
respond to the scroll wheel while Ctrl is held, and reset to their default on a right-click. The graph updates
continuously, and every change is applied to the device immediately. Click the channel again to return to the
dashboard.

**Build a crossover.** On an output channel, switch to the crossover tab. Each output provides four crossover
bands, each configured by family (Linkwitz-Riley, Butterworth or Bessel), type (low pass or high pass) and slope.
This is the configuration required to drive an active two-way or three-way system directly from the device's
outputs.

**Route your signal.** Open the matrix mixer with Ctrl+Shift+M. Rows are inputs, columns are outputs, and each
crosspoint carries an independent gain and a phase invert. Routing both input channels to a single output at
-6 dB, for example, produces a summed mono feed suitable for a subwoofer. Per-output gain, delay, mute and enable
controls sit alongside the matrix, and channels can be renamed to reflect their role in your system.

**Align your speakers.** Per-output delay is set in milliseconds from the matrix mixer or the channel editor.
Firmware compensates automatically for the differing latencies of the S/PDIF, I2S and PDM output paths, so the
values you enter correspond to acoustic delay.

**Save your work.** Adjustments are applied to the device immediately, but they reside in volatile memory until
you save them. Press Ctrl+S, or use **File > Save Preset**, to commit the current configuration to one of the
device's ten preset slots so that it survives a power cycle. An asterisk beside the preset selector indicates
unsaved changes. **File > Revert Preset** discards them and reloads the stored version, and **File > Factory
Reset** returns the device to its defaults.

---

## The main window at a glance

- **Sidebar.** Lists the inputs at the top and the outputs beneath. Selecting a channel opens its editor;
  selecting it again returns you to the dashboard. Input pairs can be linked so that edits apply to both halves of
  a stereo pair at once. Each channel carries a colour that identifies its trace on the graph.
- **Dashboard.** The default view, which presents one card per channel or channel pair, summarising the filters in
  use along with live gain, delay and mute state.
- **Graph.** A hardware-accelerated frequency response plot, rendered with Win2D, that shows the combined response
  of every visible channel. Visibility pills below the plot toggle individual channels and retain their state
  between sessions. Opening a channel editor narrows the graph to that channel; returning to the dashboard
  restores your saved configuration. The graph can also be detached into its own window, which optionally follows
  the selected channel.
- **Toolbar.** Provides direct access to the matrix mixer, settings, loudness compensation, crossfeed,
  psychoacoustic bass, the volume leveller, the statistics window and the master EQ bypass. Left-clicking a
  processing icon toggles the feature; right-clicking opens its settings window.
- **Preset selector, source selector and master volume.** These occupy the foot of the window, and comprise the
  active preset slot with its dirty indicator, the input source (USB, S/PDIF, I2S or ADAT, according to what the
  hardware supports), and the device-side master volume.

---

## Feature reference

### Parametric equalisation

- Ten parametric bands per channel, on every input and every output.
- Filter types: peaking, low shelf and high shelf at both 6 dB and 12 dB per octave, low pass, high pass, notch,
  all pass at 6 dB and 12 dB per octave, and Linkwitz Transform.
- Per-band bypass, so a band can be taken out of circuit without losing its settings.
- Linkwitz Transform is offered on output channels only, as it exists to reshape a sealed-box driver's roll-off.
  Its four parameters (driver f0 and Q0, target fp and Qp) are edited in a popover with Cancel and Apply buttons,
  so a partially entered value is never applied to your speakers. The popover reports the resulting DC boost as
  the parameters are edited.
- Input channels can be linked so that a single edit applies to both halves of a stereo pair.

### Crossovers

- Four crossover bands per output channel, independent of the parametric bands.
- Linkwitz-Riley at 12, 24, 36 and 48 dB per octave.
- Butterworth from 6 to 48 dB per octave in 6 dB steps.
- Bessel at 12, 24, 36 and 48 dB per octave.
- Family, type and slope are chosen from separate pickers, which reduces the common case (a Linkwitz-Riley
  fourth-order pair at a given frequency) to a small number of selections.

### Matrix mixer

- A full routing matrix from every input channel to every output channel, with independent gain and phase invert
  at each crosspoint.
- Per-output gain, delay, mute and enable.
- Editable channel names that propagate throughout the interface, including the dashboard, the graph legend and
  the control surface binding targets.
- A safety interlock warns before you enable outputs that contend for the same hardware resource.
- Disabled output columns are dimmed and inert, making the reason for a silent channel immediately apparent.

### Loudness compensation

Loudness compensation applies volume-dependent equalisation derived from the ISO 226 equal-loudness contours,
restoring the bass and treble that the ear loses at low listening levels. The reference SPL and the strength of the
correction are both adjustable, the resulting curve is drawn live, and on Firmware that supports it you may choose
precisely which output channels receive the compensation.

### Headphone crossfeed

Crossfeed applies a BS2B-derived process, with optional interaural time delay, that softens the unnaturally wide
channel separation of headphone listening. Three classic presets are provided (Default, Chu Moy and Jan Meier)
along with a custom mode that exposes the cutoff frequency and feed level directly. The set of output pairs that
receives crossfeed is selectable on Firmware that supports it.

### Volume leveller

The volume leveller is an RMS-based, soft-knee upward compressor that lifts quiet passages toward a target level
without ever making loud passages louder. Controls cover the amount, the speed (slow, medium or fast), the maximum
gain it is permitted to apply, a gate threshold below which it remains inactive, and an optional 10 ms lookahead
for improved transient handling. The channels that feed the shared level detector and the channels to which the
resulting gain is applied are selected independently.

### Psychoacoustic bass

Psychoacoustic bass provides missing-fundamental enhancement for small speakers, synthesising a harmonic series
that the ear interprets as bass the driver cannot physically reproduce. The cutoff frequency, harmonic level,
clipper drive, even-to-odd harmonic character and the amount of original bass retained are all adjustable, with
starting-point presets and per-output selection.

### Stereo upmixer

The upmixer derives centre and surround channels from a stereo source on RP2350 devices. Centre and surround
extraction each offer two engine modes (Sinner and Logician) with their own conditioning controls: extraction
strength, centre width, presence, correlation threshold, attack and release, detector bass cut, surround delay,
high-pass and low-pass filtering, and decorrelation. A live telemetry strip shows the measured correlation and
explains why the upmixer is parked whenever it is not producing output. Controls that do not apply to the current
mode are hidden rather than greyed out, and the matrix mixer labels the derived rows while the upmixer runs.

### Test signal generator

The test signal generator runs on the device itself, and produces sine and square tones, white and pink noise, and
logarithmic, linear or stepped sweeps for calibration and troubleshooting. The target channels and the level are
both selectable. The generator can optionally bypass the DSP chain entirely, which is useful for verifying an
output path in isolation, decorrelate the channels, or step through one channel at a time. Because it runs on the
device, it exercises the entire output path rather than the host playback stack alone.

### Control surfaces

Control surfaces bind physical controls attached to the device's spare GPIO pins to DSP parameters, so that the
device can be operated without a computer. Supported control types are buttons, switches, potentiometers, rotary
encoders, plain LEDs, PWM LEDs and infrared receivers. Each binding pairs a control with a parameter and an
action: absolute adjustment, stepped increment or decrement, toggle, set, follow, momentary, trigger, or one of
the LED indicator behaviours. Parameters include volume, mute, preset selection, input source, the processing
blocks, per-output gain and delay, and individual filter parameters. Infrared remotes are handled by a learning
mode that captures NEC, RC5 and RC6 codes directly from the handset. Channel targets are presented using your own
channel names.

### Input sources and hardware configuration

The device is not limited to USB. Depending on your hardware and Firmware, the input source selector offers USB,
S/PDIF, I2S and ADAT, and the settings window provides a page for each:

- **Mains Outputs** assigns a GPIO pin to each output, with duplicate detection and conflict warnings.
- **I²S Configuration** covers clock mode, the shared bit and word clocks, and the optional master clock.
- **S/PDIF Input** configures the receiver, including multiple selectable instances on Firmware that supports them,
  and LG Sound Sync, which decodes volume and mute messages sent by LG televisions over TOSLINK.
- **I2S Input** and **ADAT Input** configure the corresponding digital inputs.
- **ADAT Output** configures the optical multichannel output on RP2350 devices.
- **External Mute Control** drives a DAC's hardware mute pin, so that muting produces true silence rather than a
  low signal level.
- **Control Interfaces** configures the device's UART and I2C interfaces.

### Presets and files

- **Device presets.** Ten slots on the device, each with a user-defined name. Save with Ctrl+S, revert to the
  stored version, or choose which slot loads at startup. Master volume and the physical output configuration can
  each be stored globally or as part of each preset, according to your preference.
- **Preset files.** The entire device configuration can be exported to a `.dspipreset` file, which may then be
  imported at a later date or onto another device. A preset file carries the input preamps, volumes, input source,
  loudness, crossfeed, volume leveller, psychoacoustic bass, upmixer, every channel's name, delay, gain, mute and
  enable state along with its EQ and crossover bands, every matrix crosspoint, and the physical I/O wiring. Volume
  levels and I/O configuration are separate options when importing, disabled by default, and anything the
  connected device cannot accept is reported rather than discarded silently.
- **Filter files.** Filter sets can be imported and exported in the DSPi multi-channel text format or in Room EQ
  Wizard (REW) format, which allows a measured room correction to be applied directly. A channel selection dialog
  determines the channels to which an imported file is applied.
- **AutoEQ.** Search the AutoEQ database of over a thousand headphone measurements and apply a profile together
  with its recommended preamp adjustment in a single step. Frequently used models can be kept in a favourites
  menu, and the bundled database can be refreshed from within the application.

### Monitoring and diagnostics

- Peak metering on every channel, with clip indication.
- Per-core CPU load, which indicates the processing headroom remaining.
- The statistics window (Ctrl+Shift+T) reports the platform, Firmware version and serial number, the system clock,
  core voltage, sample rate and temperature, PDM and S/PDIF error counters, USB audio ring statistics, and buffer
  fill levels with high and low watermarks that can be reset on demand.
- A bulk endpoint monitor (Ctrl+Shift+B) decodes the raw control traffic between Console and the device. It is
  primarily a development aid, but it is also valuable when diagnosing an unusual configuration.

---

## Keyboard shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+I | Import filters |
| Ctrl+Shift+I | Import preset file |
| Ctrl+E | Export filters |
| Ctrl+Shift+E | Export preset file |
| Ctrl+S | Save preset |
| Ctrl+Shift+B | Browse AutoEQ profiles |
| Ctrl+Shift+L | Loudness compensation |
| Ctrl+Shift+C | Crossfeed |
| Ctrl+Shift+P | Psychoacoustic bass |
| Ctrl+Shift+U | Stereo upmixer |
| Ctrl+Shift+M | Matrix mixer |
| Ctrl+Shift+G | Test signal generator |
| Ctrl+Shift+T | Statistics |
| Alt+F4 | Exit |

Ctrl+Shift+I and Ctrl+Shift+B are each currently assigned to two menu items (control surfaces and the bulk
endpoint monitor respectively, in addition to the entries listed above). Both of those windows are always
reachable from the File menu.

Numeric fields throughout the application share the same conventions: type a value directly, hold Ctrl and scroll
to adjust it, or right-click to reset it to its default.

---

## Settings

The settings window is organised into sections:

- **General > Globals.** Determines whether master volume is stored globally or per preset, whether the device
  loads its default preset or the last used preset at startup, and whether the output configuration travels with
  presets or is saved independently.
- **Graphing > Style, Scale and Grid & Labels.** Control the appearance of the frequency response plot, including
  its frequency and amplitude ranges, gridlines, labels, and whether inactive channels are drawn as dotted traces.
- **Hardware.** Covers output pin assignment, ADAT output, I²S configuration, S/PDIF input, I2S input, ADAT input,
  external mute control and control interfaces, as described above.
- **Presets > UI.** Determines how presets are presented in the main window.
- **Advanced > Debug.** Provides diagnostic options intended for development and for investigating unexpected
  behaviour.
- **About.** Shows the application version, the platform and the Firmware version reported by the connected device.

Settings that must be written to the device are staged rather than applied piecemeal. The settings window shows a
count of pending device changes, which you may then either save to the device's flash or discard.

---

## Troubleshooting

**Console reports that no USB devices are visible to libusb.** The DSPi vendor interface has not been bound to
the WinUSB driver. Assigning WinUSB to the vendor interface with a tool such as Zadig resolves this. Note that
this applies to the vendor control interface only, and does not affect the standard USB audio interface that
Windows uses for playback.

**Controls or entire windows are missing.** Console hides any feature for which the connected device's Firmware
does not report support. This is almost always a version mismatch: confirm that the Firmware version matches the
Console version, including any beta or hotfix suffix.

**The device is not detected at all.** Verify that the cable carries data, that the device enumerates as a USB
audio interface in the Windows sound settings, and that Firmware has finished flashing (a board left in
bootloader mode presents itself as a removable drive rather than an audio device).

**Changes are lost after a power cycle.** Adjustments are applied live but are not persistent until saved. Press
Ctrl+S to write the current configuration to a preset slot.

---

## Building from source

Requirements:

- .NET 8 SDK
- Visual Studio 2022 with the .NET Desktop Development workload and the Windows App SDK C# components

```bash
dotnet build -p:Platform=x64
```

The platform must be specified explicitly: the default `AnyCPU` configuration fails because the Windows App SDK
requires an explicit runtime identifier. This project targets x86_64 only.

Alternatively, open `DSPiConsole.sln` in Visual Studio 2022 and build with Ctrl+Shift+B.

---

## Project structure

```
DSPiConsole-Windows/
├── DSPiConsole/                        # WinUI 3 application
│   ├── MainWindow.xaml(.cs)            # Sidebar, dashboard, channel and crossover editors, graph
│   ├── MatrixMixerWindow.xaml(.cs)     # Routing matrix and per-output controls
│   ├── GraphWindow.xaml(.cs)           # Detachable frequency response plot
│   ├── LoudnessWindow.xaml(.cs)        # ISO 226 loudness compensation
│   ├── CrossfeedWindow.xaml(.cs)       # BS2B headphone crossfeed
│   ├── VolumeLevellerWindow.xaml(.cs)  # Upward compression
│   ├── PsychoacousticBassWindow.xaml(.cs)
│   ├── UpmixerWindow.xaml(.cs)         # Stereo to centre and surround upmixing
│   ├── TestSignalsWindow.xaml(.cs)     # Onboard signal generator
│   ├── ControlSurfacesWindow.xaml(.cs) # GPIO and infrared control bindings
│   ├── StatsWindow.xaml(.cs)           # Telemetry and buffer statistics
│   ├── BulkMonitorWindow.xaml(.cs)     # Control traffic decoder
│   ├── Settings/                       # Settings shell, registry and pages
│   ├── Controls/                       # Bode plot, meters, CPU display
│   ├── Dialogs/                        # AutoEQ browser, channel pickers
│   ├── Services/                       # Filter and preset file handling, AutoEQ database
│   └── ViewModels/                     # Application state and device commands
├── DSPiConsole.Core/                   # Platform-independent models and DSP mathematics
│   ├── Models/                         # Channels, filters, crossovers, control surfaces, status
│   └── DspMath.cs                      # Biquad and crossover coefficient calculation
└── DSPiConsole.Usb/                    # USB transport
    ├── DspDevice.cs                    # Vendor control protocol over LibUsbDotNet
    └── BulkParamsParser.cs             # Bulk configuration decoding
```

---

## Related projects

- [DSPi](https://github.com/WeebLabs/DSPi): Firmware itself, along with the hardware documentation, the signal
  chain reference and the USB control protocol specification.
- [DSPi Console for macOS](https://github.com/WeebLabs/DSPi-Console): the macOS application.
- [DSPi Console for Linux](https://github.com/WeebLabs/DSPi-Console-Linux): a Qt and Rust port for Linux.
- [dspictl](https://github.com/WeebLabs/dspictl): command line control, which is useful for scripting and
  automation.
- [DSPiCliRemote](https://github.com/WeebLabs/DSPiCliRemote): web- and application-based remote control.

The [official Discord server](https://discord.gg/RCyqxAQ5xS) is the best place for development updates,
discussion and assistance.

---

## License and acknowledgements

Released under the GNU General Public License v3.0.

- Headphone correction profiles are drawn from the [AutoEQ project](https://github.com/jaakkopasanen/AutoEq).
- Crossfeed is derived from the BS2B algorithm.
- Loudness compensation follows the ISO 226:2003 equal-loudness contours.
