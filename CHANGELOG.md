# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-11-27

### Added
- **Windows 11 Acrylic & Mica Flyout**: Compact (~10% screen area) bottom-right overlay styled after macOS Control Center using `SetWindowCompositionAttribute` so blur remains active while unfocused.
- **Non-Activating Overlay Mode**: Uses `WS_EX_NOACTIVATE` and a temporary `WH_MOUSE_LL` outside-click hook so opening the mixer over borderless fullscreen games never triggers the Windows taskbar.
- **Per-App WASAPI Volume Mixer**: Process-grouped audio session mixer (`NAudio.Wasapi`) with cached executable icons, `FileDescription` display names, quick mute toggles, mouse-wheel slider control, and cross-session volume persistence (`%LocalAppData%\VolumeMixer\volumes.json`).
- **Per-Tab Chromium Audio Control**: Manifest V3 extension (`VolumeMixer.Extension`) paired with a loopback RFC 6455 WebSocket server (`127.0.0.1:49152`) supporting Chrome, Edge, Brave, Opera, Vivaldi, and Yandex:
  - Web Audio API `GainNode` volume control (resilient to YouTube Stable Volume normalization) with automatic CORS fallback to native element volume.
  - Per-tab favicons, automatic `.webp`/`.avif` artwork transcoding via `OffscreenCanvas`, 20s MV3 service worker heartbeat, and `100%` default volume for newly opened tabs.
- **Collapsible Lock-Screen Media Player**: Expandable (`▾`/`▴`) media drawer per playable app (WinRT SMTC) and browser tab (`navigator.mediaSession`) featuring rounded cover art, title/artist, vector transport buttons (`Previous`, `Play/Pause`, `Next`), `LIVE` indicator, and commit-on-release timeline scrubbing.
- **Global Hotkeys & System Integration**: In-place hotkey recorder (`Toggle Mixer` default `Ctrl + Shift + V`, `Master Mute`, `Active Window Mute`), silent `--tray` Windows autostart, desktop-scoped single-instance wakeup, and multi-resolution app icon.
- **CoreAudioTray Profile Bridge**: Reads/syncs `%AppData%\CoreAudioTray\config.json` for one-click Headphones / Speakers+Mic switching and split Output / Microphone selection via `IPolicyConfig`, preserving the locked profile across sessions.
- **UI Animations**: `CubicEase` window open/close scale & slide transitions, smooth drawer expansion/collapse, panel crossfade/slide, animated toggle switch, and button hover/press micro-interactions.

### Performance & Reliability
- Stopped all UI polling timers when the flyout is hidden in the system tray (`0%` background polling).
- In-place diff syncing for app sessions and browser tabs (`LastUserEditUtc` & `IsScrubbing` guards) to prevent slider drag interruptions during updates.
- Thread-safe debounced file persistence and UI-dispatcher marshaling for async favicon/artwork decoding.
