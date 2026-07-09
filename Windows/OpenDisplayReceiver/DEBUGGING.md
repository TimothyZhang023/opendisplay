# Debugging Windows receiver crashes

Use the `OpenDisplayReceiver-win-x64-debug.zip` artifact when the receiver exits unexpectedly.

## Logs

The receiver writes logs from process startup to:

```text
%LOCALAPPDATA%\OpenDisplayReceiver\Logs\latest.log
```

Each run also creates a timestamped log next to `latest.log`:

```text
OpenDisplayReceiver-YYYYMMDD-HHMMSS-PID.log
```

The main window has buttons to copy the current log path and open the logs folder. If the app exits before the window appears, open the folder above manually and attach `latest.log`.

## Debug package

The debug package is built with:

```powershell
dotnet publish . -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=portable -p:DebugSymbols=true
```

It is intentionally not a single-file publish so `.pdb` files stay next to the binaries and stack traces are easier to symbolicate.

## Useful switches

```powershell
OpenDisplayReceiver.exe --windowed
OpenDisplayReceiver.exe --renderer ffplay --windowed
OpenDisplayReceiver.exe --no-mdns --windowed
```

Use `--renderer ffplay` to isolate native Media Foundation renderer crashes. Use `--no-mdns` to isolate Bonjour/mDNS socket or firewall issues.
