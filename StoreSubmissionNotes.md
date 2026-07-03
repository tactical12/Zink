# Zink Store submission notes

Use these notes in Partner Center if the submission asks for capability or feature explanations.

## App summary

Zink is a Windows media hub for local media playback, online media access, WebView browsing, radio playback, Spotify integration, screen recording, screen sharing, calling, FPS recording, and live streaming.

## Capability explanations

`runFullTrust`

Zink is a packaged WinUI 3 desktop app. Full trust is required for desktop media, capture, recording, WebView, native audio/video pipeline, and local file workflows that are not available in a pure UWP app.

`internetClient`

Zink uses internet access for web browsing, radio streams, Spotify APIs, WebView-based third-party services, calling/signaling, diagnostics upload when the user chooses it, and streaming destinations.

`backgroundMediaPlayback`

Zink plays radio, music, and media while the app is not the foreground window.

`globalMediaControl`

Zink reads compatible system media sessions so it can display current playback status, metadata, progress, and controls for services such as Spotify. This is a user-facing media feature.

`graphicsCapture`

Zink uses Windows Graphics Capture for user-started screen sharing, screen recording, FPS recording, and streaming. Capture starts only after the user chooses a capture feature/source.

`graphicsCaptureWithoutBorder`

Zink includes an optional setting for cleaner capture in screen sharing, recording, FPS recording, and streaming scenarios. The feature is user-controlled and the app still works if borderless capture is not granted.

`microphone`

Microphone access is used for user-started calling, recording with microphone audio, and live streaming with microphone audio. Zink does not record microphone audio unless the user starts a feature that uses it.

## Third-party components and services

Zink includes or integrates with third-party tools and services for media, browsing, filtering, and streaming features. The app should disclose this in the Store description and privacy policy:

- Spotify integration uses Spotify OAuth and Spotify Web API.
- WebView pages can open third-party web services such as YouTube, Discord, Netflix, Twitch, X, TikTok, and others.
- Zink Connect includes local filtering/ad-blocking functionality for the in-app browser experience.
- Media inspection and streaming are handled without launching bundled command-line executables in the Store build.
- Codec prompts are used to help users understand missing local media codec support.
- RTSS integration is optional and used only for FPS-related features when RTSS is already installed/running. Zink does not bundle or silently install the RTSS installer.

## Reviewer notes

All sensitive features are user-started. Zink does not secretly start microphone capture, screen capture, streaming, or diagnostic uploads. Local files are opened through Windows pickers, file associations, or user-selected folders.
