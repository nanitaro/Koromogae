Koromogae  Read me first
=========================

A Windows app that routes an input (recording) device through a chain of
VST®2 / VST3 effect plugins (32-bit and 64-bit can be mixed) to an output
(playback) device, in real time. It can also record the processed sound
to WAV / FLAC.

For the full manual (with pictures), open manual.en.html in your browser.
It is in the same folder. "Help > Open manual" in the app opens it too.
Japanese: README.txt / manual.ja.html


* Requirements

  - Windows (x64)
  - .NET 10 Desktop Runtime (x64)
      Get it from: https://dotnet.microsoft.com/download/dotnet/10.0
  - Microsoft Visual C++ Redistributable (x64, the latest v14)
      Used by the bundled portaudio.dll (not bundled with this app). If your
      PC does not have it, get it from this page:
      https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist
  - To use 32-bit plugins, the x86 .NET 10 runtime is also required.


* How to use

  1. Start Koromogae.exe.
  2. Choose the "Sound API", the "Recording device" and the "Playback device".
     "WASAPI shared mode" is the easiest to begin with.
  3. To use plugins, press "Add..." and choose a .dll (VST2) or .vst3 (VST3).
     Plugins are processed from top to bottom.
  4. Press "Start" and the sound flows. Press "Stop" to stop.

  Settings and plugin states are saved when you close the app and restored
  at the next start.
  If you get dropouts, raise "Internal buffer (ms)" to +10, +20 ...
  "Options" menu: turn on "Minimize to the system tray" (off by default) and
  minimizing puts the app in the notification area; right-click the icon to
  restore it, start/stop playback and recording, or exit.
  Only one copy per data folder runs at a time; a second start brings the
  running window to the front and exits. Portable copies in different
  folders can run together.
  Startup arguments: --minimized starts minimized in the tray (a minimized
  window appears for a moment), --start starts playback automatically
  (you can give both).


* Language

  "Language" in the menu bar selects Japanese / English (applied after a
  restart). By default: Japanese if the Windows display language is
  Japanese, English otherwise.


* Where settings and logs are saved / portable mode

  By default they are saved in this folder:
      %APPDATA%\Koromogae
        settings.json ... settings and plugin states
        app.log ......... log

  Put an empty file named "portable" (or "portable.txt") next to
  Koromogae.exe, and settings.json and app.log are saved in that folder.
  You can carry the whole folder around (for example on a USB stick).


* Uninstall

  Just delete the folder. Nothing is written to the registry.
  To remove the settings as well, delete %APPDATA%\Koromogae too.


* Reporting a problem

  Please attach "Copy" from "Help > About" and app.log
  ("Help > Open log and settings folder").
  The version number is also shown in "About".


* License

  This app is released under the MIT License (LICENSE).
  For the licenses of bundled components such as PortAudio, see
  THIRD-PARTY-NOTICES.md.

  VST is a registered trademark of Steinberg Media Technologies GmbH.
  This app is not affiliated with, endorsed by, or sponsored by Steinberg
  Media Technologies GmbH.
