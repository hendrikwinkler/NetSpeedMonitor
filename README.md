# NetSpeedMonitor

A tiny upload/download speed display for the Windows 10/11 taskbar, with no dependencies. It is a modern take on the classic NetSpeedMonitor toolbar, which no longer works on Windows 11.

![NetSpeedMonitor in the Windows 11 taskbar](docs/preview.png)

- **Never covers taskbar buttons.** It can reserve its own spot in the notification area.
- **A single ~90 KB EXE.** No installation and no admin rights; it uses the .NET Framework 4 that ships with Windows.
- **Starts with Windows.** The autostart is per user and can be turned off.
- **Gaming-friendly.** It stays behind fullscreen games like the taskbar does, and stops drawing while hidden. CPU usage is not measurable; RAM use is about 15 MB.
- Uses the taskbar clock's font and follows light/dark theme and DPI scaling automatically.
- German or English UI, depending on the Windows display language.

*[Deutsche Beschreibung weiter unten](#deutsch)*

## Download & start

1. Download `NetSpeedMonitor.exe` from the [latest release](../../releases/latest).
2. Move it to a permanent folder first, e.g. `%LOCALAPPDATA%\Programs\NetSpeedMonitor`. The autostart entry points to wherever the EXE is first started.
3. Double-click it. The EXE is not code-signed, so Windows SmartScreen may warn about an unknown publisher; click *More info → Run anyway*. You can also build it yourself (see below).

Right-click the display to switch to bit/s, turn *Start with Windows* on or off, reserve a fixed spot, or exit.

## Reserving a fixed spot (recommended on Windows 11)

By default the display sits right next to the notification area and moves along when tray icons appear or disappear. On a full taskbar, however, Windows doesn't know about the display and could place buttons underneath it.

To prevent that, NetSpeedMonitor adds two invisible placeholder icons to the notification area. Once they are visible in the taskbar, the display is drawn exactly on top of them, and Windows keeps that space free permanently. This is a one-time setup:

- click **^** in the notification area and drag both NetSpeedMonitor icons onto the taskbar, **or**
- open **Settings → Personalization → Taskbar → Other system tray icons** and turn on both *NetSpeedMonitor* entries.

## Details

- Counts all connected **physical** network adapters (Ethernet, Wi-Fi). Virtual adapters (VPN, Hyper-V, WSL, …) are skipped so nothing is counted twice; VPN traffic still shows up through the physical adapter.
- Updates once per second. Byte units are 1024-based (like Explorer), bit units are 1000-based.
- Recovers automatically when Explorer restarts. The display appears on the primary taskbar.
- Requirements: Windows 11 (tested on 25H2) or Windows 10 version 1607 or newer (should work, untested), plus the .NET Framework 4.x that is preinstalled.

## Build from source

Run `build.bat`. It uses the C# compiler that ships with Windows (.NET Framework 4), so there is nothing to install. Exit a running NetSpeedMonitor first.

| File | Purpose |
|---|---|
| `NetSpeedMonitor.cs` | complete source code |
| `app.manifest` | DPI awareness, Windows 10/11 compatibility |
| `NetSpeedMonitor.ico` | app icon |
| `build.bat` | build script |

## Uninstall

1. Right-click the display, uncheck *Start with Windows*, then click *Exit*.
2. Delete the EXE.
3. Optional: remove its settings with `reg delete HKCU\Software\NetSpeedMonitor /f`.

## Antivirus false positives

Antivirus products with behavior-based detection may flag NetSpeedMonitor, for example Bitdefender with *Atc4.Detection*. The reason is that an unknown, unsigned program that adds itself to autostart and attaches to the taskbar resembles patterns that malware also uses. The complete source code is in this repository, so you can review it and build the EXE yourself.

If it gets quarantined:

1. Restore it in your antivirus. This usually adds an exception at the same time.
2. Optionally report the false positive to the vendor, e.g. to [Bitdefender](https://www.bitdefender.com/consumer/support/answer/29358/).

---

## Deutsch

Schlanke Anzeige von Upload- und Download-Geschwindigkeit in der Taskleiste von Windows 10/11. Das Tool ist ein Nachbau des klassischen NetSpeedMonitor, der unter Windows 11 nicht mehr läuft.

**Starten:** `NetSpeedMonitor.exe` aus dem [neuesten Release](../../releases/latest) herunterladen, in einen festen Ordner legen (der Autostart zeigt auf diesen Ort) und doppelklicken. Weil die EXE nicht signiert ist, warnt SmartScreen eventuell; dann auf *Weitere Informationen → Trotzdem ausführen* klicken. Das Tool trägt sich beim ersten Start in den Autostart ein. Admin-Rechte oder eine Installation sind nicht nötig.

**Rechtsklick auf die Anzeige:** In Bit/s anzeigen, Mit Windows starten, Festen Platz einrichten, Beenden.

**Fester Platz (empfohlen):** Das Tool legt zwei unsichtbare Platzhalter-Symbole im Infobereich an. Sobald sie in der Taskleiste sichtbar sind, wird die Anzeige genau darüber gezeichnet, und Taskleisten-Schaltflächen können nie darunter geraten. Dafür einmalig auf **^** klicken und beide NetSpeedMonitor-Symbole in die Taskleiste ziehen. Alternativ geht es über *Einstellungen → Personalisierung → Taskleiste → Andere Symbole der Taskleiste*, dort beide Einträge einschalten.

**Verhalten:** Gezählt werden nur physische Adapter (LAN/WLAN), damit nichts doppelt erscheint. Bei Vollbild-Spielen liegt die Anzeige wie die Taskleiste dahinter und zeichnet nichts. Die CPU-Last ist nicht messbar, der Speicherbedarf liegt bei etwa 15 MB.

**Selbst bauen:** `build.bat` ausführen; dafür muss nichts installiert werden.

**Entfernen:** Rechtsklick, *Mit Windows starten* abhaken, *Beenden*, dann die EXE löschen. Optional noch `reg delete HKCU\Software\NetSpeedMonitor /f` ausführen.

**Virenscanner:** Programme mit Verhaltenserkennung können NetSpeedMonitor fälschlich melden, zum Beispiel Bitdefender mit *Atc4.Detection*. Eine unbekannte, unsignierte EXE, die sich in den Autostart einträgt und an die Taskleiste hängt, ähnelt nämlich Mustern von Schadsoftware. Der komplette Quellcode liegt hier im Repo. Landet die EXE in der Quarantäne, im Virenscanner wiederherstellen; das legt meist gleich eine Ausnahme an. Den Fehlalarm kann man optional beim Hersteller melden, etwa bei [Bitdefender](https://www.bitdefender.com/consumer/support/answer/29358/).

---

## License

MIT, see [LICENSE](LICENSE).

Inspired by the classic NetSpeedMonitor taskbar toolbar. This is an independent re-implementation and is not affiliated with the original.
