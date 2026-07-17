# Windows Architecture Research

Research date: 2026-07-17  
Accessed: 2026-07-17  
Target: a production Windows-first recorder and non-destructive smart editor.

## Recommendation

Use **C#/.NET 10 with WinUI 3 on the stable Windows App SDK** for the application shell and orchestration. Use Windows-native capture and media APIs behind narrow interfaces:

- **Windows.Graphics.Capture** with Direct3D 11 as the primary monitor/window capture path.
- A **Desktop Duplication** fallback/prototype for cases where explicit pointer-shape metadata or lower-level monitor updates are required.
- **WASAPI shared-mode loopback** for system audio and shared-mode capture for microphone input.
- **Media Foundation** for camera ingestion and live hardware-accelerated source encoding.
- **FFmpeg 8.1.2** as a supervised external media worker for analysis, proxy creation, composition, format conversion, and final export.
- A small **C++/WinRT native bridge only when measurement proves managed interop insufficient**. Do not begin with a large native core.
- **MSIX** for the signed primary distribution, with an unpackaged developer build and a fallback installer only if driver/helper deployment requires it.

This choice optimizes the first production target rather than hypothetical portability. The project model, timeline, analysis, and export plans remain platform-neutral so a later macOS capture implementation can be added without rewriting the editor.

## Why this stack

The current machine already has .NET 9/10, Visual Studio 2022/2026, Windows App Runtime 1.6-2.3, and FFmpeg 8.1.2. Rust is absent. Microsoft identifies WinUI 3 as the recommended native framework for new Windows desktop applications, and the stable Windows App SDK 2.3.1 is production-supported as of this research date.

WinUI 3 gives native controls, UI Automation, keyboard support, Fluent styling, HWND/Win32 interop, DirectX composition, and MSIX integration. The recorder's performance-critical path should stay off the UI thread and pass GPU surfaces or encoded packets between bounded services.

## Framework comparison

| Candidate | Native capture/media integration | Editor UI and accessibility | Packaging | Cross-platform reality | Decision |
| --- | --- | --- | --- | --- | --- |
| C#/.NET + WinUI 3 | Direct WinRT/COM access; D3D interop available | Modern Windows controls, UI Automation, keyboard support | First-class MSIX/Store path | UI is Windows-specific; domain/editor model can remain portable | **Selected** |
| C#/.NET + WPF | Excellent Win32 interop and mature tooling | Mature, accessible, highly customizable; older composition model | MSIX or conventional installer | Windows-only | Fallback if WinUI blocks timeline delivery |
| C#/.NET + Avalonia | Native helpers still required for WGC/WASAPI/MF | Cross-platform XAML-like UI; custom media surface work | Platform-specific packaging | Better UI portability, not capture portability | Reconsider for macOS phase, not MVP |
| Native C++ + WinUI | Maximum API/control access | Strong performance, higher implementation and test cost | First-class | Windows-specific | Reserve for narrow bridge/proven hot paths |
| Electron + native helpers | Capture reliability still depends on native code | Fast web UI iteration; Chromium memory/process overhead | Mature installers, larger payload | UI portable; media helpers are not | Rejected for first build |
| Tauri/Rust | Native plugins required; Rust toolchain absent | Small webview shell | Mature but additional toolchain | UI portable; capture remains per-platform | Rejected for MVP |
| Qt/C++ | Strong graphics and cross-platform primitives | Capable, but C++ velocity and deployment/licensing decisions add cost | `windeployqt` plus installer | Real UI portability, separate capture integrations | Rejected for MVP |

## Capture architecture

### Primary screen path: Windows.Graphics.Capture

Windows.Graphics.Capture (WGC) supplies a user-consented `GraphicsCaptureItem`, `Direct3D11CaptureFramePool`, GPU surfaces, content size, and `SystemRelativeTime`. Microsoft documents that this timestamp uses QueryPerformanceCounter time, making it suitable as the common clock for media synchronization.

Use WGC because it:

- Captures a selected display or application window through secure system UI.
- Delivers Direct3D 11 surfaces without a CPU readback.
- Supports Windows 10 1803 and later for the picker path.
- Exposes cursor inclusion control on Windows 10 2004 and later.
- Handles window occlusion better than pixel-copy techniques.
- Supports frame-pool recreation for resize/device-loss events.

The frame callback must do minimal work: validate size, attach the QPC timestamp, enqueue the surface, and return. GPU crop/scale/composition occurs on a dedicated render worker.

### Region capture

Region capture should not rely on a separate legacy API. Capture the containing monitor with WGC, then crop on the GPU using a normalized region stored in the project. Recalculate physical pixels when DPI, monitor topology, or rotation changes. The selection overlay must not be captured; hide it before capture starts or exclude its window where supported.

### Cursor metadata

WGC can include or exclude the cursor but does not expose the high-level interaction metadata required for smart editing. Record a separate QPC-stamped cursor stream:

- screen position in physical pixels;
- button down/up and click count;
- wheel delta;
- cursor visibility and shape identifier where available;
- foreground window/process and client bounds;
- optional Raw Input deltas for smoothing analysis.

For editable cursor rendering, set `IsCursorCaptureEnabled = false` where supported and composite a captured/reconstructed pointer during preview/export. On older systems, retain the baked pointer and disable replacement features.

Desktop Duplication exposes dirty/move rectangles and pointer updates. Prototype it as a fallback and as a possible pointer-shape source, not as the default window-capture path.

### Borders and consent

The system capture border is a trust feature. Borderless capture requires explicit capability declaration and user consent; another application's requirements may still force the border. 7Record must never silently imply that the border can always be removed.

### HDR and color

Microsoft warns that HDR capture can look washed out in an 8-bit BGRA pipeline. The capture service must detect advanced color:

- MVP defaults to an SDR project and applies documented HDR-to-SDR tone mapping.
- A later HDR project mode uses `R16G16B16A16_FLOAT` end to end.
- Store source color metadata and transformation decisions in the manifest.

## Audio architecture

### System audio

Use WASAPI loopback on the selected render endpoint in shared mode. Windows documents that loopback captures the mix produced by the audio engine and does not require vendor-specific "Stereo Mix" hardware.

### Microphone

Capture the selected endpoint independently in shared mode. Negotiate the device's mix format, convert to 48 kHz float internally, and preserve the original device/format metadata.

### Synchronization and drift

Use QPC as the project clock. Every capture packet receives:

- source timestamp;
- first-frame project offset;
- sample/frame duration;
- discontinuity flags;
- device and format revision.

Do not assume microphone and render clocks remain aligned. Measure expected sample position against QPC, estimate drift over a rolling window, and apply bounded asynchronous resampling to the monitoring/export path. Preserve unmodified source audio segments.

When an endpoint changes or disappears, finalize the current segment, record a discontinuity event, and either switch with explicit UI feedback or pause recording according to user settings.

## Camera architecture

Use Media Foundation device enumeration and Source Reader/Capture Engine APIs. Prefer camera-native formats that avoid unnecessary conversion. Timestamp frames into the same QPC project clock and encode the camera as an independent source. Presenter layout is an edit decision, never baked into the screen source.

## Encoding and recoverable recording

### Live encoding

Use Media Foundation hardware encoder MFTs where available:

- H.264 is the compatibility default.
- HEVC/AV1 are optional after capability, licensing, and export validation.
- Query encoder capability rather than assuming NVENC, Intel Quick Sync, or AMD AMF availability.
- Provide a software fallback and expose encoder overload/dropped-frame health.

NVIDIA NVENC, Intel oneVPL/Quick Sync, and AMD AMF are relevant capability families, but vendor-specific SDK integration is not the first implementation. Media Foundation should select platform encoders first; direct vendor paths are later optimization experiments.

### Segmentation and journal

Never record a single long, failure-fragile file.

1. Create a project directory and atomically write `project.json`.
2. Open short independently finalized segments for screen, camera, microphone, and system audio.
3. Append metadata/events to a checksummed journal using monotonic sequence numbers.
4. Finalize segments every 2-5 seconds or at source/format discontinuities.
5. Atomically publish each segment record only after the media file is closed and probed.
6. On restart, replay the journal, discard incomplete temporary files, probe finalized segments, and reconstruct the timeline.

The prototype must compare fragmented MP4 and short standard MP4 segments. If finalization behavior is unreliable, use Matroska for intermediate recording and remux on export. The project format must not depend on a single container index surviving a crash.

## FFmpeg's role

Run FFmpeg as a supervised worker, not inside the UI process. It provides:

- media probing;
- proxy and thumbnail generation;
- waveform and silence analysis;
- loudness measurement/normalization;
- timeline composition;
- caption burn-in or muxing;
- final MP4/WebM/GIF output;
- hardware export encoders (`h264_nvenc`, `h264_qsv`, `h264_amf`) with software fallback.

Capture should continue if an analysis worker crashes. Export failures must preserve logs and return explicit diagnostics. Pin and redistribute a known FFmpeg build with license notices rather than depending on a machine-global executable.

## Module boundaries

```text
7Record.App                 WinUI shell, navigation, commands, accessibility
7Record.Domain              Project/timeline/event model; no Windows UI dependency
7Record.Capture.Abstractions Source contracts, clocks, health, capabilities
7Record.Capture.Windows     WGC, WASAPI, Media Foundation, cursor/foreground metadata
7Record.Recording           Segments, journal, manifests, recovery
7Record.Media               Probe, proxy, waveform, thumbnails, FFmpeg supervision
7Record.Analysis            Silence, loading, cursor intent, caption jobs
7Record.Editor              Timeline operations, undo/redo, automation decisions
7Record.Export              Render plans, presets, encoder capability/fallback
7Record.Infrastructure      Logging, settings, storage, diagnostics
7Record.Tests               Domain, recovery, media fixtures, UI automation
```

Dependencies point inward: UI and platform adapters depend on domain contracts; domain code never depends on WinUI, FFmpeg process details, or Windows capture classes.

## Project and timeline model

Store immutable source references plus reversible edit events:

- screen/camera/audio segment references and clock ranges;
- cursor, click, keyboard-opt-in, foreground-window, and source-health events;
- caption words and confidence;
- zoom/pan, cursor emphasis, scene, silence, loading-speed, audio, and framing decisions;
- user overrides and disabled automatic suggestions.

Use stable IDs, schema versions, and deterministic migrations. Preview and export consume the same render plan so the final file cannot silently differ from the editor.

## Preview and proxy strategy

- Keep source media immutable.
- Generate lower-resolution intraframe-friendly proxies after recording.
- Cache thumbnails and waveforms by source hash and algorithm version.
- Render preview using Direct3D/Composition surfaces; do not decode or analyze on the UI thread.
- Use bounded queues and drop preview frames rather than blocking capture or audio.
- Keep capture and editor processes separable so a later isolated recorder process can be introduced without changing contracts.

## Packaging, signing, and updates

Use signed MSIX for clean install/uninstall, identity, differential updates, Store/enterprise deployment, and capability declarations. Keep application data and projects outside the install location. Validate:

- packaged and unpackaged developer modes;
- FFmpeg redistribution and invocation;
- capture picker and borderless capability behavior;
- code signing and timestamping;
- update during an existing project library;
- uninstall without deleting user projects.

## Accessibility

WinUI XAML controls provide baseline UI Automation and keyboard behavior. Custom timeline controls need explicit automation peers, accessible names, patterns, focus order, high-contrast rendering, scalable text, and non-color event labels. Automated accessibility checks are part of CI, supplemented by Narrator and keyboard-only release tests.

## Testing strategy

### Deterministic unit tests

- project migrations and validation;
- time/range math and ripple operations;
- zoom/loading/silence event generation;
- journal replay and corrupt-tail handling;
- encoder capability selection and fallback.

### Media fixture tests

- variable frame rate and dropped frames;
- 44.1/48/96 kHz devices and clock drift;
- endpoint changes and camera disconnects;
- portrait/rotated/mixed-DPI monitors;
- HDR source to SDR project;
- one-hour synthetic recording with sync assertions.

### Fault injection

- terminate the process during every segment/journal state;
- device removal and D3D device loss;
- disk full, permission failure, and path disappearance;
- FFmpeg crash/hang with timeout and diagnostic capture;
- encoder overload and hardware-encoder initialization failure.

### UI and accessibility tests

- source selection and readiness;
- global shortcuts and pause/resume;
- keyboard-only edit/export;
- UI Automation tree and Narrator smoke tests;
- high contrast, 200% scaling, and reduced motion.

## Highest-risk prototypes

1. **Clock/sync prototype:** WGC + WASAPI loopback + microphone + camera for 60 minutes; measure end-to-end drift and dropped frames.
2. **Recovery prototype:** 2-5 second segments plus journal; kill the process at random points and quantify recoverable duration.
3. **GPU path prototype:** WGC D3D11 surface to hardware H.264 without CPU readback; validate NVIDIA, Intel, AMD, and software fallback.
4. **Cursor prototype:** cursor-excluded WGC plus QPC metadata and reconstructed pointer; compare against baked cursor at mixed DPI.
5. **WinUI editor prototype:** Direct3D preview plus virtualized timeline; maintain interaction latency while proxies generate.
6. **Packaging prototype:** signed test MSIX containing the media worker and required capabilities.

No advanced editor feature should be scheduled before the first three prototypes meet explicit thresholds.

## Architecture acceptance thresholds

- 1080p60 screen capture for 60 minutes with less than 0.1% dropped frames on the reference machine.
- Screen/mic/system-audio/camera sync error remains below 40 ms at 60 minutes.
- Forced termination loses no more than the active 5-second segment.
- Capture CPU remains below 20% on the reference machine when hardware encoding is active.
- Capture continues when proxy/analysis workers are terminated.
- Preview interaction remains below 100 ms for common timeline operations.
- Every source and automation event can be disabled or restored without modifying source media.

## Primary sources

All sources were accessed on 2026-07-17.

### Windows capture and graphics

- https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturepicker?view=winrt-28000
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool?view=winrt-28000
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime?view=winrt-28000
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled?view=winrt-28000
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired?view=winrt-28000
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccess.requestaccessasync?view=winrt-28000
- https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
- https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range

### Audio, camera, and media

- https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/capturing-a-stream
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/rendering-a-stream
- https://learn.microsoft.com/en-us/windows/win32/medfound/microsoft-media-foundation-sdk
- https://learn.microsoft.com/en-us/windows/win32/medfound/source-reader
- https://learn.microsoft.com/en-us/windows/win32/medfound/sink-writer
- https://learn.microsoft.com/en-us/windows/win32/medfound/video-encoder
- https://ffmpeg.org/ffmpeg.html
- https://ffmpeg.org/ffmpeg-devices.html
- https://ffmpeg.org/ffmpeg-formats.html

### Hardware encoding

- https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html
- https://gpuopen.com/advanced-media-framework/
- https://github.com/intel/libvpl
- https://oneapi-spec.uxlfoundation.org/specifications/oneapi/latest/elements/onevpl/source/index

### UI, packaging, and alternatives

- https://learn.microsoft.com/en-us/windows/apps/winui/winui3/
- https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/
- https://docs.avaloniaui.net/docs/welcome
- https://www.electronjs.org/docs/latest/tutorial/accessibility
- https://www.electronjs.org/docs/latest/tutorial/performance
- https://v2.tauri.app/start/
- https://doc.qt.io/qt-6/windows-deployment.html
- https://learn.microsoft.com/en-us/windows/msix/overview
- https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview

## Limitations and uncertainty

- Direct vendor encoder SDKs may outperform Media Foundation or expose better controls, but the integration cost is not justified before profiling.
- WinUI 3 timeline virtualization and Direct3D composition must be prototyped; WPF remains a credible fallback shell.
- Exact behavior across protected content, elevated windows, Remote Desktop, HDR displays, and multi-GPU laptops requires hardware testing.
- The intermediate container choice remains open until crash-recovery experiments compare short MP4, fragmented MP4, and Matroska segments.
- macOS requires ScreenCaptureKit, Core Audio, AVFoundation, and a separate shell strategy; only domain/editor portability is promised by this decision.
