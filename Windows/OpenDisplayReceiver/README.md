# OpenDisplay Receiver for Windows

This is a first Windows receiver for the existing OpenDisplay Mac sender protocol. It listens on TCP `:9000`, sends the same `hello` control message as the iOS receiver, keeps the Mac watchdog alive with `ping`, and pipes the Mac's low-latency H.264 Annex B stream into `ffplay`.

The Mac is still the sender: it creates the virtual display with `CGVirtualDisplay`, captures it with ScreenCaptureKit, and streams H.264 frames over the existing length-prefixed TCP protocol.

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK
- FFmpeg with `ffplay.exe` available on `PATH`
- Windows firewall access for TCP `9000` on private networks

## Build and run

From this directory:

```powershell
dotnet run -c Release -- --width 1920 --height 1080 --scale 2
```

Useful options:

```powershell
--width 2560          # pixels announced to the Mac
--height 1440
--scale 2            # the Mac creates a HiDPI display at pixels / 2 points
--port 9000
--name "Windows Display"
--ffplay "C:\ffmpeg\bin\ffplay.exe"
```

## Connect from the Mac

This first Windows receiver uses the Mac sender's manual endpoint override:

```sh
defaults write com.peetzweg.opensidecar.mac host "WINDOWS_IP"
defaults write com.peetzweg.opensidecar.mac port "9000"
open -a OpenDisplay
```

For Debug builds from Xcode, use the debug bundle id instead:

```sh
defaults write com.peetzweg.opensidecar.mac.debug host "WINDOWS_IP"
defaults write com.peetzweg.opensidecar.mac.debug port "9000"
```

Clear the override later with:

```sh
defaults delete com.peetzweg.opensidecar.mac host
defaults delete com.peetzweg.opensidecar.mac port
```

## Cursor note

The iOS receiver draws the Mac cursor locally from `cursor` / `cursorImg` control messages. This first Windows receiver relies on `ffplay`, so it cannot draw that local overlay yet. To make the cursor visible on Windows, tell the Mac sender to include the cursor in the captured video:

```sh
defaults write com.peetzweg.opensidecar.mac localCursor -bool false
```

For Debug builds:

```sh
defaults write com.peetzweg.opensidecar.mac.debug localCursor -bool false
```

Restart the Mac app after changing this setting.

## Current scope

Implemented:

- TCP receiver on `:9000`
- Existing OpenDisplay length-prefixed frame protocol
- `hello`, `ping`, `pong`, and periodic `stats` control messages
- H.264 Annex B stream forwarding to `ffplay`
- Stable install id stored in `%APPDATA%\OpenDisplayReceiver\install-id.txt`
- Manual Mac endpoint setup instructions printed at startup

Not implemented yet:

- Bonjour/mDNS discovery from the Mac device list
- Native Windows H.264 renderer
- Local cursor overlay rendering
- Touch / pointer input back to macOS
- Audio forwarding
