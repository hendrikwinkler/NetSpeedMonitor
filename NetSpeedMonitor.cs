// NetSpeedMonitor – schlanker Nachbau des klassischen NetSpeedMonitor für Windows 10/11.
// Zeigt Upload- und Download-Geschwindigkeit direkt in der Taskleiste an.
//
// So funktioniert es:
//  * Die Anzeige ist ein kleines, transparentes Fenster, das der Taskleiste gehört (Owner = Shell_TrayWnd).
//    Es liegt dadurch immer direkt über der Taskleiste, verschwindet mit ihr hinter Vollbild-Anwendungen
//    und bekommt nie den Fokus.
//  * Fester Platz: Das Programm legt zwei unsichtbare Platzhalter-Symbole im Infobereich an. Sobald diese in
//    der Taskleiste sichtbar sind, wird die Anzeige genau darüber gezeichnet. Windows hält den Platz dann frei,
//    Taskleisten-Schaltflächen können nie darunter geraten. Solange die Platzhalter im Überlaufmenü ("^")
//    stecken, sitzt die Anzeige direkt links neben dem Infobereich.
//  * Gezählt wird der Verkehr aller physischen Netzwerkadapter (keine virtuellen/VPN-Adapter, dadurch wird
//    nichts doppelt gezählt).
//
// Kompilieren: build.bat (verwendet den in Windows enthaltenen C#-Compiler, keine Installation nötig).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("NetSpeedMonitor")]
[assembly: AssemblyDescription("Upload- und Download-Geschwindigkeit in der Taskleiste")]
[assembly: AssemblyProduct("NetSpeedMonitor")]
[assembly: AssemblyCopyright("Copyright (c) 2026 hendrikwinkler, MIT License")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace NetSpeedMonitor
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\NetSpeedMonitor.SingleInstance", out createdNew))
            {
                if (!createdNew) return;   // läuft bereits
                try
                {
                    using (var app = new App())
                        app.Run();
                }
                catch (Exception ex) { Log.Error(ex); }   // protokollieren statt Absturzdialog
            }
        }
    }

    sealed class App : IDisposable
    {
        const string HostClass = "NetSpeedMonitor.Host";
        const string OverlayClass = "NetSpeedMonitor.Overlay";
        const uint WM_TRAYICON = Native.WM_APP + 1;
        const uint WM_TRAYCHECKED = Native.WM_APP + 2;
        static readonly IntPtr TimerSample = (IntPtr)1;
        static readonly IntPtr TimerPlacement = (IntPtr)2;
        static readonly IntPtr TimerWatch = (IntPtr)3;
        const int CmdBits = 1, CmdAutostart = 2, CmdReserve = 3, CmdExit = 4;

        readonly IntPtr instance;
        readonly Native.WndProc hostProc, overlayProc;   // Delegates festhalten, sonst räumt der GC sie weg
        readonly uint wmTaskbarCreated;
        readonly Settings settings;
        readonly NetCounter counter = new NetCounter();
        readonly SpeedRenderer renderer = new SpeedRenderer();
        readonly TrayInspector inspector = new TrayInspector();
        readonly TrayPlaceholders placeholders;

        IntPtr host, overlay, overlayOwner, taskbar;
        bool overlayVisible, needsRender = true, placementPending, darkTaskbar, reservedActive, promotionRemembered;
        Native.RECT shownBounds, watchedTaskbar, watchedTray;
        bool watchedTopmost;
        int shownDpi, missingIconTicks;
        double lastUp, lastDown;
        string upText, downText;

        public App()
        {
            instance = Native.GetModuleHandle(null);
            hostProc = HostWndProc;
            overlayProc = OverlayWndProc;
            wmTaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            settings = Settings.Load();
            darkTaskbar = Theme.IsTaskbarDark();

            RegisterWindowClass(HostClass, hostProc);
            RegisterWindowClass(OverlayClass, overlayProc);
            host = Native.CreateWindowEx(Native.WS_EX_TOOLWINDOW, HostClass, "NetSpeedMonitor", Native.WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (host == IntPtr.Zero) throw new Win32Exception();

            if (!settings.Initialized)
            {
                Autostart.Set(true);   // beim ersten Start automatisch mit Windows starten
                settings.Initialized = true;
                settings.Save();
            }
            else Autostart.UpdatePath();

            placeholders = new TrayPlaceholders(host, WM_TRAYICON);
            placeholders.Add(TaskbarDpi(), PlaceholdersLikelyVisible());

            counter.Sample(out lastDown, out lastUp);   // Startwerte der Zähler
            UpdateTexts();
            Native.SetTimer(host, TimerSample, 1000, IntPtr.Zero);
            Native.SetTimer(host, TimerWatch, 250, IntPtr.Zero);
            Refresh();
        }

        public void Run()
        {
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }

        public void Dispose()
        {
            placeholders.Remove();
            DestroyOverlay();
            if (host != IntPtr.Zero && Native.IsWindow(host)) Native.DestroyWindow(host);
            host = IntPtr.Zero;
            renderer.Dispose();
            placeholders.Dispose();
            counter.Dispose();
        }

        void RegisterWindowClass(string name, Native.WndProc proc)
        {
            var wc = new Native.WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX));
            wc.lpfnWndProc = proc;
            wc.hInstance = instance;
            wc.hCursor = Native.LoadCursor(IntPtr.Zero, (IntPtr)Native.IDC_ARROW);
            wc.lpszClassName = name;
            if (Native.RegisterClassEx(ref wc) == 0) throw new Win32Exception();
        }

        // ---------------------------------------------------------------- Fensterprozeduren

        IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == wmTaskbarCreated && msg != 0)
                {
                    OnTaskbarCreated();
                    return IntPtr.Zero;
                }
                switch (msg)
                {
                    case Native.WM_TIMER:
                        if (wParam == TimerSample) OnSampleTimer();
                        else if (wParam == TimerWatch) OnWatchTimer();
                        else if (wParam == TimerPlacement)
                        {
                            Native.KillTimer(hWnd, TimerPlacement);
                            placementPending = false;
                            Refresh();
                        }
                        return IntPtr.Zero;
                    case WM_TRAYICON:
                        OnTrayIcon(lParam);
                        return IntPtr.Zero;
                    case WM_TRAYCHECKED:
                        Refresh();
                        return IntPtr.Zero;
                    case Native.WM_SETTINGCHANGE:
                        bool dark = Theme.IsTaskbarDark();
                        if (dark != darkTaskbar) { darkTaskbar = dark; needsRender = true; }
                        SchedulePlacement();
                        break;
                    case Native.WM_DISPLAYCHANGE:
                        SchedulePlacement();
                        break;
                    case Native.WM_DESTROY:
                        Native.PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case Native.WM_MOUSEACTIVATE:
                        return (IntPtr)Native.MA_NOACTIVATE;
                    case Native.WM_RBUTTONUP:
                        ShowMenu();
                        return IntPtr.Zero;
                    case Native.WM_DPICHANGED:
                        return IntPtr.Zero;   // Größe und Position bestimmen wir selbst
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // ---------------------------------------------------------------- Ereignisse

        void OnSampleTimer()
        {
            counter.Sample(out lastDown, out lastUp);
            UpdateTexts();
            Refresh();
        }

        // Statt systemweiter Ereignis-Hooks: viermal pro Sekunde ein paar billige Abfragen (~0,01 ms). Ändern sich
        // Lage oder "Topmost"-Zustand der Taskleiste (z. B. Vollbild-Anwendung) oder die Breite des Infobereichs,
        // wird die Anzeige sofort neu ausgerichtet; alles andere erledigt der Sekundentakt.
        void OnWatchTimer()
        {
            Native.RECT tbRect = new Native.RECT(), trayRect = new Native.RECT();
            bool topmost = false;
            IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
            if (tb != IntPtr.Zero)
            {
                Native.GetWindowRect(tb, out tbRect);
                topmost = (Native.GetWindowLong(tb, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
                IntPtr tray = Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
                if (tray != IntPtr.Zero) Native.GetWindowRect(tray, out trayRect);
            }
            if (tbRect.Equals(watchedTaskbar) && trayRect.Equals(watchedTray) && topmost == watchedTopmost) return;
            watchedTaskbar = tbRect;
            watchedTray = trayRect;
            watchedTopmost = topmost;
            Refresh();
        }

        void UpdateTexts()
        {
            string up = Format.Speed(lastUp, settings.UseBits);
            string down = Format.Speed(lastDown, settings.UseBits);
            if (up != upText || down != downText)
            {
                upText = up;
                downText = down;
                needsRender = true;
            }
        }

        void OnTrayIcon(IntPtr lParam)
        {
            // NOTIFYICON_VERSION_4: das Ereignis steht im unteren Wort von lParam
            int evt = (int)((long)lParam & 0xFFFF);
            if (evt == Native.WM_CONTEXTMENU || evt == Native.NIN_SELECT || evt == Native.NIN_KEYSELECT)
                ShowMenu();
        }

        void OnTaskbarCreated()
        {
            // Explorer wurde (neu) gestartet oder die Taskleiste neu aufgebaut
            placeholders.Add(TaskbarDpi(), PlaceholdersLikelyVisible());
            inspector.Reset();
            taskbar = IntPtr.Zero;
            DestroyOverlay();
            SchedulePlacement();
        }

        // Stehen die Platzhalter voraussichtlich in der Taskleiste, legen wir sie gleich unsichtbar an (kein Aufblitzen)
        bool PlaceholdersLikelyVisible()
        {
            return settings.Reserved || placeholders.PromotedInRegistry();
        }

        void SchedulePlacement()
        {
            if (placementPending || host == IntPtr.Zero) return;
            placementPending = true;
            Native.SetTimer(host, TimerPlacement, 15, IntPtr.Zero);
        }

        // ---------------------------------------------------------------- Platzierung

        struct Placement
        {
            public IntPtr Taskbar;
            public Native.RECT Bounds;
            public int Dpi;
            public bool Reserved, Covered;
        }

        void Refresh()
        {
            Placement p;
            if (!ComputePlacement(out p))
            {
                HideOverlay();
                return;
            }
            if (p.Covered)
            {
                // Vollbild-Anwendung (z. B. Spiel) verdeckt die Taskleiste: nichts zeichnen, nur die Reihenfolge sichern
                if (overlay != IntPtr.Zero && Native.IsWindow(overlay) && overlayOwner == p.Taskbar)
                    KeepAboveTaskbar(p.Taskbar, shownBounds);
                return;
            }
            reservedActive = p.Reserved;
            if (p.Reserved != settings.Reserved)
            {
                settings.Reserved = p.Reserved;   // beim nächsten Start die Platzhalter gleich passend anlegen
                settings.Save();
            }
            if (p.Reserved && !promotionRemembered)
            {
                promotionRemembered = true;
                placeholders.RememberPromoted();
            }
            if (!EnsureOverlay(p.Taskbar)) return;

            if (needsRender || p.Dpi != shownDpi || p.Bounds.Width != shownBounds.Width || p.Bounds.Height != shownBounds.Height)
            {
                renderer.Render(p.Bounds.Width, p.Bounds.Height, p.Dpi, !p.Reserved, darkTaskbar, settings.UseBits, upText, downText);
                renderer.Present(overlay, p.Bounds.Left, p.Bounds.Top);
            }
            else if (!p.Bounds.Equals(shownBounds))
                renderer.Present(overlay, p.Bounds.Left, p.Bounds.Top);
            shownBounds = p.Bounds;
            shownDpi = p.Dpi;
            needsRender = false;

            KeepAboveTaskbar(p.Taskbar, p.Bounds);   // schon vor dem Einblenden richtig einsortieren
            if (!overlayVisible)
            {
                Native.ShowWindow(overlay, Native.SW_SHOWNOACTIVATE);
                overlayVisible = true;
                KeepAboveTaskbar(p.Taskbar, p.Bounds);
            }
        }

        // Die Anzeige muss direkt über der Taskleiste liegen: nie darunter und nie über einem Fenster, das die
        // Taskleiste verdeckt (Vollbild-Video, Spiel …). Als "Besitz" der Taskleiste folgt sie ihr normalerweise
        // von selbst; das hier fängt nur Sonderfälle ab (z. B. Anzeige neu erstellt, während ein Vollbild läuft).
        void KeepAboveTaskbar(IntPtr tb, Native.RECT bounds)
        {
            IntPtr w = Native.GetWindow(overlay, Native.GW_HWNDNEXT);
            for (int i = 0; i < 64 && w != IntPtr.Zero && w != tb; i++)
            {
                Native.RECT r;
                if (Native.IsWindowVisible(w) && Native.GetWindowRect(w, out r) && Native.RECT.Intersect(r, bounds).Width > 0) break;
                w = Native.GetWindow(w, Native.GW_HWNDNEXT);
            }
            if (w == tb) return;

            IntPtr insertAfter = Native.GetWindow(tb, Native.GW_HWNDPREV);
            if (insertAfter == overlay) return;
            // SWP_NOOWNERZORDER: nur unser Fenster verschieben, niemals die Taskleiste selbst
            Native.SetWindowPos(overlay, insertAfter == IntPtr.Zero ? Native.HWND_TOP : insertAfter, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        bool ComputePlacement(out Placement p)
        {
            p = new Placement();
            IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
            if (tb == IntPtr.Zero || !Native.IsWindowVisible(tb)) return false;
            taskbar = tb;
            IntPtr trayNotify = Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);

            Native.RECT tbRect;
            if (!Native.GetWindowRect(tb, out tbRect) || tbRect.Width <= 0 || tbRect.Height <= 0) return false;
            bool horizontal = tbRect.Width >= tbRect.Height;

            // Automatisch ausgeblendete Taskleiste: nur anzeigen, solange sie ganz sichtbar ist
            Native.RECT monitor;
            if (!GetMonitorRect(tb, out monitor)) return false;
            Native.RECT visible = Native.RECT.Intersect(tbRect, monitor);
            if (horizontal ? visible.Height < tbRect.Height : visible.Width < tbRect.Width) return false;

            int dpi = (int)Native.GetDpiForWindow(tb);
            if (dpi < 96) dpi = 96;
            p.Taskbar = tb;
            p.Dpi = dpi;
            if (TaskbarCovered(tb, tbRect))
            {
                p.Covered = true;
                return true;
            }

            Native.RECT trayRect = new Native.RECT();
            bool haveTray = trayNotify != IntPtr.Zero && Native.GetWindowRect(trayNotify, out trayRect)
                            && trayRect.Width > 0 && trayRect.Height > 0;

            // 1) Platzhalter sichtbar in der Taskleiste -> Anzeige genau darüber (reservierter Platz)
            Native.RECT slots;
            TrayGeometry geometry = placeholders.Query(tbRect, horizontal, out slots);
            missingIconTicks = geometry == TrayGeometry.Missing ? missingIconTicks + 1 : 0;
            if (missingIconTicks >= 5)
            {
                // Symbole fehlen (z. B. war Explorer beim Start noch nicht bereit) -> neu anlegen
                missingIconTicks = 0;
                placeholders.Add(dpi, PlaceholdersLikelyVisible());
            }
            if (geometry == TrayGeometry.Slots && IsReservedArea(tb, slots, trayRect, haveTray, horizontal))
            {
                placeholders.SetBlank(true, dpi);
                p.Bounds = slots;
                p.Reserved = true;
                return true;
            }
            placeholders.SetBlank(false, dpi);

            // 2) Sonst direkt links neben dem Infobereich (inkl. "^"-Pfeil)
            int anchor;
            if (haveTray) anchor = horizontal ? trayRect.Left : trayRect.Top;
            else if (geometry == TrayGeometry.Hidden) anchor = horizontal ? slots.Left : slots.Top;   // Position des "^"
            else anchor = horizontal ? tbRect.Right : tbRect.Bottom;

            Native.SIZE content = renderer.Measure(dpi, settings.UseBits);
            if (horizontal)
                p.Bounds = new Native.RECT(anchor - content.cx, tbRect.Top, anchor, tbRect.Bottom);
            else
                p.Bounds = new Native.RECT(tbRect.Left, anchor - content.cy, tbRect.Right, anchor);
            return true;
        }

        bool IsReservedArea(IntPtr tb, Native.RECT slots, Native.RECT trayRect, bool haveTray, bool horizontal)
        {
            // Versteckte Symbole melden die Position des "^"-Pfeils, und der sitzt immer ganz vorne im
            // Infobereich. Liegen die Platzhalter woanders, sind sie eindeutig sichtbar.
            if (haveTray && Math.Abs(horizontal ? slots.Left - trayRect.Left : slots.Top - trayRect.Top) > 2)
                return true;
            // Sonst ist die Lage mehrdeutig (z. B. ein Platzhalter direkt neben dem "^") -> per UI Automation klären
            bool? confirmed = inspector.Check(tb, slots, placeholders.Tooltips, host, WM_TRAYCHECKED);
            return confirmed == true;
        }

        // Liegt eine Vollbild-Anwendung vor der Taskleiste, nimmt Explorer ihr "Topmost". Die Anzeige ist dann
        // ohnehin unsichtbar – außer dem Zählen fällt keinerlei Arbeit an.
        static bool TaskbarCovered(IntPtr tb, Native.RECT tbRect)
        {
            if ((Native.GetWindowLong(tb, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0) return false;
            IntPtr fg = Native.GetForegroundWindow();
            Native.RECT r;
            return fg != IntPtr.Zero && fg != tb && Native.GetWindowRect(fg, out r) && r.Contains(tbRect);
        }

        static bool GetMonitorRect(IntPtr hwnd, out Native.RECT rect)
        {
            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            bool ok = Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), ref mi);
            rect = mi.rcMonitor;
            return ok;
        }

        static int TaskbarDpi()
        {
            IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
            int dpi = tb == IntPtr.Zero ? 96 : (int)Native.GetDpiForWindow(tb);
            return dpi < 96 ? 96 : dpi;
        }

        // ---------------------------------------------------------------- Anzeige-Fenster

        bool EnsureOverlay(IntPtr owner)
        {
            if (overlay != IntPtr.Zero && Native.IsWindow(overlay) && overlayOwner == owner) return true;
            DestroyOverlay();
            // Owner = Taskleiste: liegt dadurch über ihr und übernimmt ihren "Topmost"-Zustand (kein eigenes WS_EX_TOPMOST,
            // sonst läge die Anzeige bei Vollbild-Anwendungen darüber)
            overlay = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                OverlayClass, "NetSpeedMonitor", Native.WS_POPUP, 0, 0, 0, 0, owner, IntPtr.Zero, instance, IntPtr.Zero);
            if (overlay == IntPtr.Zero) return false;
            overlayOwner = owner;
            overlayVisible = false;
            needsRender = true;
            shownBounds = new Native.RECT();
            return true;
        }

        void DestroyOverlay()
        {
            if (overlay != IntPtr.Zero && Native.IsWindow(overlay)) Native.DestroyWindow(overlay);
            overlay = IntPtr.Zero;
            overlayOwner = IntPtr.Zero;
            overlayVisible = false;
        }

        void HideOverlay()
        {
            if (overlayVisible && overlay != IntPtr.Zero && Native.IsWindow(overlay))
                Native.ShowWindow(overlay, Native.SW_HIDE);
            overlayVisible = false;
        }

        // ---------------------------------------------------------------- Kontextmenü

        void ShowMenu()
        {
            IntPtr menu = Native.CreatePopupMenu();
            try
            {
                Native.AppendMenu(menu, settings.UseBits ? Native.MF_CHECKED : 0, (IntPtr)CmdBits, Text.MenuBits);
                Native.AppendMenu(menu, Autostart.IsEnabled() ? Native.MF_CHECKED : 0, (IntPtr)CmdAutostart, Text.MenuAutostart);
                if (!reservedActive)
                    Native.AppendMenu(menu, 0, (IntPtr)CmdReserve, Text.MenuReserve);
                Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
                Native.AppendMenu(menu, 0, (IntPtr)CmdExit, Text.MenuExit);

                // Menü öffnet sich von der Taskleiste weg (unten -> nach oben)
                Native.POINT pt;
                Native.GetCursorPos(out pt);
                Native.RECT tbRect, monitor;
                bool taskbarOnTop = taskbar != IntPtr.Zero && Native.GetWindowRect(taskbar, out tbRect)
                                    && GetMonitorRect(taskbar, out monitor)
                                    && tbRect.Width >= tbRect.Height && tbRect.Top + tbRect.Bottom < monitor.Top + monitor.Bottom;
                uint flags = Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_RIGHTBUTTON | Native.TPM_RIGHTALIGN
                             | (taskbarOnTop ? Native.TPM_TOPALIGN : Native.TPM_BOTTOMALIGN);

                Theme.ApplyMenuTheme(darkTaskbar);
                Native.SetForegroundWindow(host);
                int cmd = Native.TrackPopupMenuEx(menu, flags, pt.X, pt.Y, host, IntPtr.Zero);
                Native.PostMessage(host, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

                switch (cmd)
                {
                    case CmdBits:
                        settings.UseBits = !settings.UseBits;
                        settings.Save();
                        UpdateTexts();
                        needsRender = true;
                        Refresh();
                        break;
                    case CmdAutostart:
                        if (!Autostart.Set(!Autostart.IsEnabled()))
                            Native.MessageBox(host, Text.AutostartFailed, "NetSpeedMonitor",
                                Native.MB_ICONWARNING | Native.MB_TOPMOST | Native.MB_SETFOREGROUND);
                        break;
                    case CmdReserve:
                        ExplainReservedSpace();
                        break;
                    case CmdExit:
                        placeholders.Remove();
                        Native.DestroyWindow(host);
                        break;
                }
            }
            finally { Native.DestroyMenu(menu); }
        }

        void ExplainReservedSpace()
        {
            int result = Native.MessageBox(host, Text.ReserveText, Text.ReserveTitle,
                Native.MB_YESNO | Native.MB_ICONINFORMATION | Native.MB_TOPMOST | Native.MB_SETFOREGROUND);
            if (result == Native.IDYES)
            {
                try { Process.Start("ms-settings:taskbar"); }
                catch (Exception ex) { Log.Error(ex); }
            }
        }
    }

    // ==================================================================== Netzwerkzähler

    // Liest die Byte-Zähler der aktiven physischen Netzwerkadapter. Die vergleichsweise teure Liste aller
    // Schnittstellen (~1 ms) wird nur alle 10 s neu erstellt; jede Sekunde werden nur die aktiven Adapter
    // einzeln abgefragt (~0,04 ms je Adapter).
    sealed class NetCounter : IDisposable
    {
        // Offsets in MIB_IF_ROW2 (netioapi.h); die Zeilen einer MIB_IF_TABLE2 beginnen nach 8 Byte Kopf
        internal const int TableHeader = 8, RowSize = 1352;
        internal const int OffLuid = 0, OffIndex = 8, OffAlias = 28, OffType = 1128, OffFlags = 1152, OffOperStatus = 1156;
        internal const int OffInOctets = 1208, OffOutOctets = 1280;
        internal const byte FlagHardwareInterface = 0x01, FlagFilterInterface = 0x02;
        const int IF_TYPE_SOFTWARE_LOOPBACK = 24, IfOperStatusUp = 1;
        const double RescanSeconds = 10;

        sealed class Adapter { public ulong In, Out; public bool Primed; }

        readonly Dictionary<long, Adapter> adapters = new Dictionary<long, Adapter>();
        readonly List<long> found = new List<long>(), gone = new List<long>();
        readonly IntPtr row = Marshal.AllocHGlobal(RowSize);
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastTime = -1, lastScan = double.NegativeInfinity;

        public void Sample(out double downBytesPerSec, out double upBytesPerSec)
        {
            downBytesPerSec = upBytesPerSec = 0;
            double now = clock.Elapsed.TotalSeconds;
            if (now - lastScan >= RescanSeconds)
            {
                Rescan();
                lastScan = now;
            }

            ulong deltaIn = 0, deltaOut = 0;
            foreach (var pair in adapters)
            {
                Marshal.WriteInt64(row, OffLuid, pair.Key);
                Marshal.WriteInt32(row, OffIndex, 0);
                if (Native.GetIfEntry2(row) != 0) continue;
                ulong inOctets = (ulong)Marshal.ReadInt64(row, OffInOctets);
                ulong outOctets = (ulong)Marshal.ReadInt64(row, OffOutOctets);
                Adapter a = pair.Value;
                if (a.Primed)
                {
                    if (inOctets >= a.In) deltaIn += inOctets - a.In;      // bei Zähler-Reset einfach neu ansetzen
                    if (outOctets >= a.Out) deltaOut += outOctets - a.Out;
                }
                a.In = inOctets;
                a.Out = outOctets;
                a.Primed = true;
            }

            if (lastTime >= 0 && now > lastTime)
            {
                downBytesPerSec = deltaIn / (now - lastTime);
                upBytesPerSec = deltaOut / (now - lastTime);
            }
            lastTime = now;
        }

        // Nur echte, verbundene Netzwerkkarten: keine NDIS-Filter-Einträge, virtuellen/VPN-Adapter oder Loopback.
        // So wird nichts doppelt gezählt (VPN-Verkehr läuft ohnehin über die physische Karte).
        void Rescan()
        {
            IntPtr table;
            if (Native.GetIfTable2(out table) != 0) return;
            found.Clear();
            try
            {
                int count = Marshal.ReadInt32(table);
                for (int i = 0; i < count; i++)
                {
                    IntPtr r = table + TableHeader + i * RowSize;
                    byte flags = Marshal.ReadByte(r, OffFlags);
                    if ((flags & FlagHardwareInterface) == 0 || (flags & FlagFilterInterface) != 0) continue;
                    if (Marshal.ReadInt32(r, OffType) == IF_TYPE_SOFTWARE_LOOPBACK) continue;
                    if (Marshal.ReadInt32(r, OffOperStatus) != IfOperStatusUp) continue;
                    found.Add(Marshal.ReadInt64(r, OffLuid));
                }
            }
            finally { Native.FreeMibTable(table); }

            foreach (long luid in found)
                if (!adapters.ContainsKey(luid)) adapters[luid] = new Adapter();
            foreach (long luid in adapters.Keys)
                if (!found.Contains(luid)) gone.Add(luid);
            foreach (long luid in gone) adapters.Remove(luid);
            gone.Clear();
        }

        public void Dispose() { Marshal.FreeHGlobal(row); }
    }

    static class Format
    {
        static readonly string[] ByteUnits = { "KB/s", "MB/s", "GB/s" };
        static readonly string[] BitUnits = { "kbit/s", "Mbit/s", "Gbit/s" };

        public static string Speed(double bytesPerSecond, bool bits)
        {
            string[] units = bits ? BitUnits : ByteUnits;
            double step = bits ? 1000 : 1024;
            double value = bits ? bytesPerSecond * 8 / 1000 : bytesPerSecond / 1024;
            int unit = 0;
            while (value >= 999.5 && unit < units.Length - 1)
            {
                value /= step;
                unit++;
            }
            string number = value < 99.95
                ? value.ToString("0.0", CultureInfo.CurrentCulture)
                : value.ToString("0", CultureInfo.CurrentCulture);
            return number + " " + units[unit];
        }

        // Breiteste mögliche Werte – daraus ergibt sich eine feste Breite, damit nichts hin und her springt
        public static IEnumerable<string> WidestSamples(bool bits)
        {
            string sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            foreach (string unit in bits ? BitUnits : ByteUnits)
            {
                yield return "99" + sep + "9 " + unit;
                yield return "999 " + unit;
            }
        }
    }

    // ==================================================================== Darstellung

    // Zeichnet den Text mit GDI-Graustufenglättung (so scharf wie die Uhr der Taskleiste) als Deckungsmaske
    // und färbt ihn anschließend mit Alpha-Kanal ein – für ein Fenster mit echter Transparenz.
    sealed class SpeedRenderer : IDisposable
    {
        const float FontPoints = 9f;          // wie die Uhr in der Taskleiste
        const float ArrowScale = 0.75f;       // Pfeilgröße relativ zur Schrift
        const float PadLoose = 4f, PadTight = 1f, Gap = 3f;   // logische Pixel (96 dpi)
        static readonly string[] TextFonts = { "Segoe UI Variable Text", "Segoe UI" };
        static readonly string[] IconFonts = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };

        readonly IntPtr memDC;
        IntPtr dib, oldBitmap, bits;
        int width, height;
        int[] pixels = new int[0];

        // Schriften und daraus abgeleitete Maße (für die aktuelle Pixelgröße)
        IntPtr textFont, arrowFont;
        int fontPx = -1, arrowWidth, linePitch, digitTop, digitBottom, arrowOffset;
        string arrowUp, arrowDown;

        public SpeedRenderer()
        {
            memDC = Native.CreateCompatibleDC(IntPtr.Zero);
        }

        static int BasePx(int dpi) { return (int)Math.Round(FontPoints * dpi / 72f); }

        // Natürliche Größe der Anzeige (für die Platzierung neben dem Infobereich)
        public Native.SIZE Measure(int dpi, bool bits)
        {
            float scale = dpi / 96f;
            EnsureFonts(BasePx(dpi));
            int pad = (int)Math.Round(PadLoose * scale);
            return new Native.SIZE { cx = ContentWidth(bits, scale) + 2 * pad, cy = 2 * linePitch + 2 * pad };
        }

        public void Render(int w, int h, int dpi, bool loosePadding, bool dark, bool bits, string up, string down)
        {
            EnsureSurface(w, h);
            float scale = dpi / 96f;
            int pad = (int)Math.Round((loosePadding ? PadLoose : PadTight) * scale);
            EnsureFonts(FitFontPx(w, h, dpi, bits, pad, scale));
            int content = ContentWidth(bits, scale);

            Native.PatBlt(memDC, 0, 0, w, h, Native.BLACKNESS);
            Native.SetBkMode(memDC, Native.TRANSPARENT);
            Native.SetTextColor(memDC, 0xFFFFFF);

            int left = (w - content) / 2;
            int right = left + content;
            int blockHeight = linePitch + digitBottom - digitTop;   // von Oberkante Zeile 1 bis Unterkante Zeile 2
            int y1 = (h - blockHeight) / 2 - digitTop;
            int y2 = y1 + linePitch;

            DrawText(arrowFont, arrowUp, left, y1 + arrowOffset);
            DrawText(arrowFont, arrowDown, left, y2 + arrowOffset);
            DrawText(textFont, up, right - TextWidth(textFont, up), y1);
            DrawText(textFont, down, right - TextWidth(textFont, down), y2);
            Native.GdiFlush();
            Colorize(dark);
        }

        public void Present(IntPtr hwnd, int x, int y)
        {
            var dst = new Native.POINT { X = x, Y = y };
            var size = new Native.SIZE { cx = width, cy = height };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, memDC, ref src, 0, ref blend, Native.ULW_ALPHA);
        }

        // Größte Schrift (max. 9 pt), die in den Platz passt; Ergebnis wird bis zur nächsten Größenänderung gemerkt
        int fitW = -1, fitH, fitDpi, fitPad, fitPx;
        bool fitBits;

        int FitFontPx(int w, int h, int dpi, bool bits, int pad, float scale)
        {
            if (w == fitW && h == fitH && dpi == fitDpi && bits == fitBits && pad == fitPad) return fitPx;
            int px = BasePx(dpi);
            while (px > 7)
            {
                EnsureFonts(px);
                if (ContentWidth(bits, scale) <= w - 2 * pad && linePitch + digitBottom - digitTop <= h - 2) break;
                px--;
            }
            fitW = w; fitH = h; fitDpi = dpi; fitBits = bits; fitPad = pad; fitPx = px;
            return px;
        }

        int ContentWidth(bool bits, float scale)
        {
            int valueWidth = 0;
            foreach (string s in Format.WidestSamples(bits))
                valueWidth = Math.Max(valueWidth, TextWidth(textFont, s));
            return arrowWidth + (int)Math.Round(Gap * scale) + valueWidth;
        }

        // Weißer Text auf Schwarz ergibt die Deckung je Pixel -> vormultipliziertes ARGB in Textfarbe
        void Colorize(bool dark)
        {
            int n = width * height;
            Marshal.Copy(bits, pixels, 0, n);
            for (int i = 0; i < n; i++)
            {
                int c = pixels[i] & 0xFF;
                int a = c == 0 ? 1 : c;   // Alpha 1: unsichtbar, aber die Fläche bleibt für Rechtsklicks erreichbar
                pixels[i] = dark ? (a << 24) | (c << 16) | (c << 8) | c : a << 24;
            }
            Marshal.Copy(pixels, 0, bits, n);
        }

        void DrawText(IntPtr font, string s, int x, int y)
        {
            IntPtr old = Native.SelectObject(memDC, font);
            Native.TextOut(memDC, x, y, s, s.Length);
            Native.SelectObject(memDC, old);
        }

        int TextWidth(IntPtr font, string s)
        {
            IntPtr old = Native.SelectObject(memDC, font);
            Native.SIZE size;
            Native.GetTextExtentPoint32(memDC, s, s.Length, out size);
            Native.SelectObject(memDC, old);
            return size.cx;
        }

        void EnsureFonts(int px)
        {
            if (px == fontPx && textFont != IntPtr.Zero) return;
            FreeFonts();
            textFont = CreateFirstAvailable(TextFonts, px);
            IntPtr icons = CreateFirstAvailable(IconFonts, (int)Math.Round(px * ArrowScale));
            if (icons != IntPtr.Zero)
            {
                arrowFont = icons;
                arrowUp = "\uE74A";     // Pfeil nach oben (Windows-Symbolschrift)
                arrowDown = "\uE74B";   // Pfeil nach unten
            }
            else
            {
                arrowFont = textFont;
                arrowUp = "\u2191";
                arrowDown = "\u2193";
            }
            fontPx = px;

            IntPtr old = Native.SelectObject(memDC, textFont);
            Native.TEXTMETRIC tm;
            Native.GetTextMetrics(memDC, out tm);
            Native.SelectObject(memDC, old);
            linePitch = tm.tmHeight;
            arrowWidth = Math.Max(TextWidth(arrowFont, arrowUp), TextWidth(arrowFont, arrowDown));

            // Tatsächliche Ober-/Unterkante der Ziffern und Pfeile, damit beides exakt mittig sitzt
            int arrowTop, arrowBottom;
            InkRows(textFont, "0123456789", out digitTop, out digitBottom);
            InkRows(arrowFont, arrowUp + arrowDown, out arrowTop, out arrowBottom);
            arrowOffset = (int)Math.Round((digitTop + digitBottom) / 2.0 - (arrowTop + arrowBottom) / 2.0);
        }

        // Erste und letzte Pixelzeile, in der der Text wirklich "Tinte" hat (relativ zur TextOut-Position)
        void InkRows(IntPtr font, string s, out int top, out int bottom)
        {
            int w = TextWidth(font, s) + 4, h = linePitch * 2 + 4;
            IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
            var bi = new Native.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.biWidth = w;
            bi.biHeight = -h;
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            IntPtr scratchBits;
            IntPtr bitmap = Native.CreateDIBSection(dc, ref bi, 0, out scratchBits, IntPtr.Zero, 0);
            var buffer = new int[w * h];
            if (bitmap != IntPtr.Zero)
            {
                IntPtr oldBmp = Native.SelectObject(dc, bitmap);
                IntPtr oldFont = Native.SelectObject(dc, font);
                Native.PatBlt(dc, 0, 0, w, h, Native.BLACKNESS);
                Native.SetBkMode(dc, Native.TRANSPARENT);
                Native.SetTextColor(dc, 0xFFFFFF);
                Native.TextOut(dc, 2, 0, s, s.Length);
                Native.GdiFlush();
                Marshal.Copy(scratchBits, buffer, 0, buffer.Length);
                Native.SelectObject(dc, oldFont);
                Native.SelectObject(dc, oldBmp);
                Native.DeleteObject(bitmap);
            }
            Native.DeleteDC(dc);

            top = -1;
            bottom = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if ((buffer[y * w + x] & 0xFF) > 60)
                    {
                        if (top < 0) top = y;
                        bottom = y + 1;
                        break;
                    }
            if (top < 0) { top = 0; bottom = linePitch; }
        }

        static IntPtr CreateFirstAvailable(string[] faces, int px)
        {
            foreach (string face in faces)
            {
                IntPtr font = Native.CreateFont(-px, 0, 0, 0, 400, 0, 0, 0, Native.DEFAULT_CHARSET, 0, 0,
                    Native.ANTIALIASED_QUALITY, 0, face);
                if (font == IntPtr.Zero) continue;
                if (HasFace(font, face)) return font;
                Native.DeleteObject(font);   // nicht installiert -> GDI hätte eine Ersatzschrift gewählt
            }
            return IntPtr.Zero;
        }

        static bool HasFace(IntPtr font, string face)
        {
            IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
            IntPtr old = Native.SelectObject(dc, font);
            var name = new StringBuilder(64);
            Native.GetTextFace(dc, name.Capacity, name);
            Native.SelectObject(dc, old);
            Native.DeleteDC(dc);
            return string.Equals(name.ToString(), face, StringComparison.OrdinalIgnoreCase);
        }

        void EnsureSurface(int w, int h)
        {
            if (dib != IntPtr.Zero && w == width && h == height) return;
            FreeSurface();
            var bi = new Native.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.biWidth = w;
            bi.biHeight = -h;   // top-down
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            dib = Native.CreateDIBSection(memDC, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) throw new Win32Exception();
            oldBitmap = Native.SelectObject(memDC, dib);
            width = w;
            height = h;
            if (pixels.Length < w * h) pixels = new int[w * h];
        }

        void FreeSurface()
        {
            if (dib == IntPtr.Zero) return;
            Native.SelectObject(memDC, oldBitmap);
            Native.DeleteObject(dib);
            dib = IntPtr.Zero;
        }

        void FreeFonts()
        {
            if (arrowFont != IntPtr.Zero && arrowFont != textFont) Native.DeleteObject(arrowFont);
            if (textFont != IntPtr.Zero) Native.DeleteObject(textFont);
            arrowFont = textFont = IntPtr.Zero;
        }

        public void Dispose()
        {
            FreeSurface();
            FreeFonts();
            Native.DeleteDC(memDC);
        }
    }

    // ==================================================================== Platzhalter im Infobereich

    enum TrayGeometry { Unknown, Missing, Hidden, Slots }

    sealed class TrayPlaceholders : IDisposable
    {
        public const int Count = 2;
        const int FirstId = 1;
        const int AppIconResource = 32512;

        readonly IntPtr hwnd;
        readonly uint callbackMessage;
        public readonly string[] Tooltips;
        IntPtr appIcon, blankIcon;
        int iconDpi;
        bool blank;

        public TrayPlaceholders(IntPtr hwnd, uint callbackMessage)
        {
            this.hwnd = hwnd;
            this.callbackMessage = callbackMessage;
            Tooltips = new string[Count];
            for (int i = 0; i < Count; i++)
                Tooltips[i] = string.Format(Text.Placeholder, i + 1, Count);
        }

        public void Add(int dpi, bool startBlank)
        {
            EnsureIcons(dpi);
            blank = startBlank;
            for (int i = 0; i < Count; i++)
            {
                var d = NewData(i);
                d.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
                d.uCallbackMessage = callbackMessage;
                d.hIcon = blank ? blankIcon : appIcon;
                d.szTip = Tooltips[i];
                if (!Native.Shell_NotifyIcon(Native.NIM_ADD, ref d))
                    Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);   // existiert schon
                d.uVersion = Native.NOTIFYICON_VERSION_4;
                Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref d);
            }
        }

        public void Remove()
        {
            for (int i = 0; i < Count; i++)
            {
                var d = NewData(i);
                Native.Shell_NotifyIcon(Native.NIM_DELETE, ref d);
            }
        }

        // Sichtbar in der Taskleiste: leeres Symbol (die Anzeige liegt darüber).
        // Im Überlaufmenü: normales Symbol, damit man es erkennt und in die Taskleiste ziehen kann.
        public void SetBlank(bool value, int dpi)
        {
            if (value == blank && dpi == iconDpi) return;
            EnsureIcons(dpi);
            blank = value;
            for (int i = 0; i < Count; i++)
            {
                var d = NewData(i);
                d.uFlags = Native.NIF_ICON;
                d.hIcon = blank ? blankIcon : appIcon;
                Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
            }
        }

        public TrayGeometry Query(Native.RECT taskbar, bool horizontal, out Native.RECT area)
        {
            area = new Native.RECT();
            var rects = new Native.RECT[Count];
            for (int i = 0; i < Count; i++)
            {
                var id = new Native.NOTIFYICONIDENTIFIER();
                id.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONIDENTIFIER));
                id.hWnd = hwnd;
                id.uID = (uint)(FirstId + i);
                if (Native.Shell_NotifyIconGetRect(ref id, out rects[i]) != 0) return TrayGeometry.Missing;
                // außerhalb der Taskleiste = im geöffneten Überlaufmenü
                if (rects[i].Width <= 0 || rects[i].Height <= 0 || !taskbar.Contains(rects[i])) return TrayGeometry.Unknown;
            }

            bool allSame = true;
            for (int i = 1; i < Count; i++) allSame &= rects[i].Equals(rects[0]);
            if (allSame)
            {
                area = rects[0];   // versteckte Symbole melden alle die Position des "^"-Pfeils
                return TrayGeometry.Hidden;
            }

            Array.Sort(rects, (a, b) => horizontal ? a.Left.CompareTo(b.Left) : a.Top.CompareTo(b.Top));
            for (int i = 1; i < Count; i++)
            {
                bool adjacent = horizontal
                    ? Math.Abs(rects[i].Left - rects[i - 1].Right) <= 2 && rects[i].Top == rects[0].Top && rects[i].Bottom == rects[0].Bottom
                    : Math.Abs(rects[i].Top - rects[i - 1].Bottom) <= 2 && rects[i].Left == rects[0].Left && rects[i].Right == rects[0].Right;
                if (!adjacent) return TrayGeometry.Unknown;
            }
            area = Native.RECT.Union(rects[0], rects[Count - 1]);
            return TrayGeometry.Slots;
        }

        // Windows 11 merkt sich pro Symbol, ob es in der Taskleiste angezeigt wird ("IsPromoted"). Daran sehen wir
        // schon beim Start, ob die Platzhalter sichtbar sein werden (-> gleich unsichtbar anlegen).
        public bool PromotedInRegistry()
        {
            int promoted = 0;
            try
            {
                foreach (RegistryKey key in OwnSettingsKeys(false))
                    using (key)
                        if (IsPromoted(key)) promoted++;
            }
            catch (Exception ex) { Log.Error(ex); }
            return promoted >= Count;
        }

        // Hat der Benutzer die Platzhalter in die Taskleiste gezogen, speichert Windows nur die Reihenfolge.
        // Wir halten den Zustand zusätzlich dort fest, wo ihn auch die Einstellungen-App ablegt – damit er
        // Neustarts sicher übersteht und die Einstellungen ihn korrekt als "an" zeigen.
        public void RememberPromoted()
        {
            try
            {
                foreach (RegistryKey key in OwnSettingsKeys(true))
                    using (key)
                        if (!IsPromoted(key)) key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        static bool IsPromoted(RegistryKey key)
        {
            object value = key.GetValue("IsPromoted");
            return value is int && (int)value == 1;
        }

        static IEnumerable<RegistryKey> OwnSettingsKeys(bool writable)
        {
            string exe = Autostart.ExePath;
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", false))
            {
                if (root == null) yield break;   // gibt es erst ab Windows 11
                foreach (string name in root.GetSubKeyNames())
                {
                    RegistryKey key = null;
                    try
                    {
                        key = root.OpenSubKey(name, writable);
                        if (key == null) continue;
                        string path = key.GetValue("ExecutablePath") as string;
                        object uid = key.GetValue("UID");
                        if (path != null && uid is int && (int)uid >= FirstId && (int)uid < FirstId + Count
                            && string.Equals(ExpandKnownFolder(path), exe, StringComparison.OrdinalIgnoreCase))
                        {
                            RegistryKey match = key;
                            key = null;
                            yield return match;
                        }
                    }
                    finally { if (key != null) key.Dispose(); }
                }
            }
        }

        // Explorer speichert Pfade teilweise als "{KnownFolder-GUID}\Rest"
        static string ExpandKnownFolder(string path)
        {
            if (path.Length > 38 && path[0] == '{' && path[37] == '}')
            {
                Guid folder;
                IntPtr p;
                if (Guid.TryParse(path.Substring(0, 38), out folder) && Native.SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out p) == 0)
                {
                    try { return Marshal.PtrToStringUni(p) + path.Substring(38); }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            return path;
        }

        Native.NOTIFYICONDATA NewData(int index)
        {
            var d = new Native.NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
            d.hWnd = hwnd;
            d.uID = (uint)(FirstId + index);
            return d;
        }

        void EnsureIcons(int dpi)
        {
            if (appIcon != IntPtr.Zero && dpi == iconDpi) return;
            FreeIcons();
            int size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, (uint)dpi);
            if (size <= 0) size = 16;
            appIcon = Native.LoadImage(Native.GetModuleHandle(null), (IntPtr)AppIconResource, Native.IMAGE_ICON, size, size, 0);
            if (appIcon == IntPtr.Zero) appIcon = Native.LoadIcon(IntPtr.Zero, (IntPtr)Native.IDI_APPLICATION);

            // Praktisch unsichtbares Symbol (Alpha 1/255): für das Auge leer, für Windows aber kein "leeres" Symbol
            var color = new byte[size * size * 4];
            for (int i = 3; i < color.Length; i += 4) color[i] = 1;
            IntPtr colorBitmap = Native.CreateBitmap(size, size, 1, 32, color);
            IntPtr mask = Native.CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
            var info = new Native.ICONINFO { fIcon = true, hbmMask = mask, hbmColor = colorBitmap };
            blankIcon = Native.CreateIconIndirect(ref info);
            Native.DeleteObject(mask);
            Native.DeleteObject(colorBitmap);
            iconDpi = dpi;
        }

        void FreeIcons()
        {
            if (appIcon != IntPtr.Zero) Native.DestroyIcon(appIcon);
            if (blankIcon != IntPtr.Zero) Native.DestroyIcon(blankIcon);
            appIcon = blankIcon = IntPtr.Zero;
        }

        public void Dispose() { FreeIcons(); }
    }

    // Prüft per UI Automation, ob die Platzhalter wirklich in der Taskleiste (und nicht im Überlaufmenü)
    // stehen. Nur nötig, wenn die Geometrie allein nicht eindeutig ist; läuft in einem Hintergrund-Thread.
    sealed class TrayInspector
    {
        readonly object sync = new object();
        Native.RECT checkedArea;
        bool hasResult, result, running;

        public bool? Check(IntPtr taskbar, Native.RECT area, string[] names, IntPtr notifyWindow, uint notifyMessage)
        {
            lock (sync)
            {
                if (hasResult && checkedArea.Equals(area)) return result;
                if (running) return null;
                running = true;
            }
            var thread = new Thread(() =>
            {
                bool ok = false;
                try { ok = Uia.AllNamesInTree(taskbar, names); }
                catch (Exception ex) { Log.Error(ex); }
                lock (sync)
                {
                    checkedArea = area;
                    result = ok;
                    hasResult = true;
                    running = false;
                }
                Native.PostMessage(notifyWindow, notifyMessage, IntPtr.Zero, IntPtr.Zero);
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            return null;
        }

        public void Reset()
        {
            lock (sync) hasResult = false;
        }
    }

    static class Uia
    {
        const int UIA_NamePropertyId = 30005;
        const int TreeScope_Descendants = 4;

        public static bool AllNamesInTree(IntPtr window, string[] names)
        {
            var automation = (IUIAutomation)new CUIAutomation();
            IUIAutomationElement root;
            if (automation.ElementFromHandle(window, out root) != 0 || root == null) return false;
            try
            {
                foreach (string name in names)
                {
                    IntPtr condition;
                    if (automation.CreatePropertyCondition(UIA_NamePropertyId, name, out condition) != 0) return false;
                    try
                    {
                        IUIAutomationElement found;
                        if (root.FindFirst(TreeScope_Descendants, condition, out found) != 0 || found == null) return false;
                        Marshal.ReleaseComObject(found);
                    }
                    finally { Marshal.Release(condition); }
                }
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(root);
                Marshal.ReleaseComObject(automation);
            }
        }

        [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
        class CUIAutomation { }

        // Nur die benötigten Methoden; die Platzhalter halten die Reihenfolge der vtable ein.
        [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomation
        {
            void CompareElements();
            void CompareRuntimeIds();
            void GetRootElement();
            [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
            void ElementFromPoint();
            void GetFocusedElement();
            void GetRootElementBuildCache();
            void ElementFromHandleBuildCache();
            void ElementFromPointBuildCache();
            void GetFocusedElementBuildCache();
            void CreateTreeWalker();
            void ControlViewWalker();
            void ContentViewWalker();
            void RawViewWalker();
            void RawViewCondition();
            void ControlViewCondition();
            void ContentViewCondition();
            void CreateCacheRequest();
            void CreateTrueCondition();
            void CreateFalseCondition();
            [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IntPtr condition);
        }

        [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomationElement
        {
            void SetFocus();
            void GetRuntimeId();
            [PreserveSig] int FindFirst(int scope, IntPtr condition, out IUIAutomationElement found);
        }
    }

    // ==================================================================== Texte

    // Oberflächentexte: Deutsch, wenn Windows auf Deutsch läuft, sonst Englisch
    static class Text
    {
        static readonly bool German = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

        public static readonly string MenuBits = German ? "In Bit/s anzeigen" : "Show in bit/s";
        public static readonly string MenuAutostart = German ? "Mit Windows starten" : "Start with Windows";
        public static readonly string MenuReserve = German
            ? "Festen Platz in der Taskleiste einrichten\u2026"
            : "Reserve a fixed spot in the taskbar\u2026";
        public static readonly string MenuExit = German ? "Beenden" : "Exit";
        public static readonly string Placeholder = German
            ? "NetSpeedMonitor (Platzhalter {0}/{1})"
            : "NetSpeedMonitor (placeholder {0}/{1})";
        public static readonly string AutostartFailed = German
            ? "Der Autostart-Eintrag konnte nicht geändert werden.\n\n" +
              "Vermutlich sperrt ihn ein Virenscanner. Füge NetSpeedMonitor dort als Ausnahme hinzu " +
              "(ggf. danach Windows neu starten) und versuche es erneut."
            : "The autostart entry could not be changed.\n\n" +
              "An antivirus program is probably blocking it. Add NetSpeedMonitor as an exception there " +
              "(restart Windows if needed) and try again.";
        public static readonly string ReserveTitle = German
            ? "NetSpeedMonitor \u2013 fester Platz"
            : "NetSpeedMonitor \u2013 fixed spot";
        public static readonly string ReserveText = German
            ? "Damit die Anzeige einen festen Platz bekommt und nie Taskleisten-Schaltflächen überdeckt, " +
              "müssen die beiden Platzhalter-Symbole von NetSpeedMonitor im Infobereich sichtbar sein.\n\n" +
              "So geht's:\n" +
              "\u2022  Auf \u201E^\u201C im Infobereich klicken und die beiden NetSpeedMonitor-Symbole in die Taskleiste ziehen, oder\n" +
              "\u2022  Einstellungen \u2192 Personalisierung \u2192 Taskleiste \u2192 \u201EAndere Symbole der Taskleiste\u201C \u2192 " +
              "beide Einträge \u201ENetSpeedMonitor\u201C einschalten.\n\n" +
              "Die Anzeige wechselt danach sofort an diesen Platz.\n\n" +
              "Einstellungen jetzt öffnen?"
            : "To give the display a fixed spot that never covers taskbar buttons, " +
              "the two NetSpeedMonitor placeholder icons must be visible in the notification area.\n\n" +
              "How:\n" +
              "\u2022  Click \u201C^\u201D in the notification area and drag both NetSpeedMonitor icons onto the taskbar, or\n" +
              "\u2022  Settings \u2192 Personalization \u2192 Taskbar \u2192 \u201COther system tray icons\u201D \u2192 " +
              "turn on both \u201CNetSpeedMonitor\u201D entries.\n\n" +
              "The display moves there right away.\n\n" +
              "Open Settings now?";
    }

    // ==================================================================== Einstellungen, Autostart, Theme

    sealed class Settings
    {
        const string KeyPath = @"Software\NetSpeedMonitor";
        public bool UseBits, Initialized, Reserved;   // Reserved: zuletzt lag die Anzeige auf den Platzhaltern

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key != null)
                    {
                        s.UseBits = Flag(key, "UseBits");
                        s.Initialized = Flag(key, "Initialized");
                        s.Reserved = Flag(key, "Reserved");
                    }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            return s;
        }

        static bool Flag(RegistryKey key, string name)
        {
            object value = key.GetValue(name);
            return value is int && (int)value != 0;
        }

        public void Save()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key.SetValue("UseBits", UseBits ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("Initialized", Initialized ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("Reserved", Reserved ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { Log.Error(ex); }   // nicht speichern zu können ist kein Grund abzustürzen
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string ValueName = "NetSpeedMonitor";

        public static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
        static string Command { get { return "\"" + ExePath + "\""; } }

        // Nur "an", wenn der Eintrag auch wirklich auf diese EXE zeigt
        public static bool IsEnabled()
        {
            try
            {
                string current;
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
                    current = run == null ? null : run.GetValue(ValueName) as string;
                if (current == null || !string.Equals(current.Trim('"', ' '), ExePath, StringComparison.OrdinalIgnoreCase))
                    return false;
                using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                {
                    // im Task-Manager bzw. in den Einstellungen deaktiviert: erstes Byte ungerade
                    var data = approved == null ? null : approved.GetValue(ValueName) as byte[];
                    if (data != null && data.Length > 0 && (data[0] & 1) != 0) return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return false;
            }
        }

        // false, wenn sich der Eintrag nicht ändern ließ (z. B. weil ein Virenscanner ihn sperrt)
        public static bool Set(bool enable)
        {
            try
            {
                using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enable) run.SetValue(ValueName, Command);
                    else run.DeleteValue(ValueName, false);
                }
                using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    if (approved != null) approved.DeleteValue(ValueName, false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return false;
            }
        }

        // Wurde die EXE verschoben, zeigt der Autostart-Eintrag auf den neuen Ort
        public static void UpdatePath()
        {
            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    string current = run == null ? null : run.GetValue(ValueName) as string;
                    if (current != null && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                        run.SetValue(ValueName, Command);
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }

    static class Theme
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int SetPreferredAppModeFn(int mode);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void FlushMenuThemesFn();
        static SetPreferredAppModeFn setPreferredAppMode;
        static FlushMenuThemesFn flushMenuThemes;
        static bool menuThemeLoaded;

        public static bool IsTaskbarDark()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object value = key == null ? null : key.GetValue("SystemUsesLightTheme");
                return !(value is int) || (int)value == 0;
            }
        }

        // Kontextmenü passend zur Taskleiste hell/dunkel. Nutzt undokumentierte uxtheme-Funktionen
        // (Windows 10 1903+), daher rein optional und abgesichert.
        public static void ApplyMenuTheme(bool dark)
        {
            try
            {
                if (!menuThemeLoaded)
                {
                    menuThemeLoaded = true;
                    if (Environment.OSVersion.Version.Build < 18362) return;
                    IntPtr uxtheme = Native.LoadLibrary("uxtheme.dll");
                    if (uxtheme == IntPtr.Zero) return;
                    IntPtr p135 = Native.GetProcAddress(uxtheme, (IntPtr)135);
                    IntPtr p136 = Native.GetProcAddress(uxtheme, (IntPtr)136);
                    if (p135 != IntPtr.Zero)
                        setPreferredAppMode = (SetPreferredAppModeFn)Marshal.GetDelegateForFunctionPointer(p135, typeof(SetPreferredAppModeFn));
                    if (p136 != IntPtr.Zero)
                        flushMenuThemes = (FlushMenuThemesFn)Marshal.GetDelegateForFunctionPointer(p136, typeof(FlushMenuThemesFn));
                }
                if (setPreferredAppMode == null) return;
                setPreferredAppMode(dark ? 2 : 3);   // ForceDark / ForceLight
                if (flushMenuThemes != null) flushMenuThemes();
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }

    static class Log
    {
        static int written;

        public static void Error(Exception ex)
        {
            if (++written > 50) return;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "NetSpeedMonitor.log"),
                    DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    // ==================================================================== Win32

    static class Native
    {
        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002, WM_SETTINGCHANGE = 0x001A, WM_MOUSEACTIVATE = 0x0021,
            WM_DISPLAYCHANGE = 0x007E, WM_TIMER = 0x0113, WM_RBUTTONUP = 0x0205, WM_DPICHANGED = 0x02E0, WM_APP = 0x8000;
        public const int WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
        public const int MA_NOACTIVATE = 3;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        public const int GWL_EXSTYLE = -20;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public const int ULW_ALPHA = 2;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        public const int IDC_ARROW = 32512, IDI_APPLICATION = 32512, IMAGE_ICON = 1, SM_CXSMICON = 49;
        public const uint MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
        public const uint TPM_RIGHTBUTTON = 0x2, TPM_RIGHTALIGN = 0x8, TPM_TOPALIGN = 0x0, TPM_BOTTOMALIGN = 0x20,
            TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
        public const uint MB_YESNO = 0x4, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        public const int IDYES = 6;
        public const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        public const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
        public const int NOTIFYICON_VERSION_4 = 4;
        public const uint BLACKNESS = 0x00000042, DEFAULT_CHARSET = 1, ANTIALIASED_QUALITY = 4;
        public const int TRANSPARENT = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public RECT(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
            public bool Contains(RECT r) { return r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom; }
            public static RECT Intersect(RECT a, RECT b)
            {
                var r = new RECT(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
                return r.Width > 0 && r.Height > 0 ? r : new RECT();
            }
            public static RECT Union(RECT a, RECT b)
            {
                return new RECT(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
            }
        }

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TEXTMETRIC
        {
            public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth,
                tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
            public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
            public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public int uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int exitCode);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapse, IntPtr func);
        [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hWnd, IntPtr id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT dst, ref SIZE size,
            IntPtr hdcSrc, ref POINT src, int colorKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string text);
        [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] public static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] public static extern IntPtr LoadImage(IntPtr instance, IntPtr name, int type, int cx, int cy, int flags);
        [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage,
            out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);
        [DllImport("gdi32.dll")] public static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic,
            uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool TextOut(IntPtr hdc, int x, int y, string s, int length);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool GetTextExtentPoint32(IntPtr hdc, string s, int length, out SIZE size);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool GetTextMetrics(IntPtr hdc, out TEXTMETRIC tm);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern int GetTextFace(IntPtr hdc, int count, StringBuilder face);
        [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, uint color);
        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
        [DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
        [DllImport("shell32.dll")] public static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        [DllImport("iphlpapi.dll")] public static extern int GetIfTable2(out IntPtr table);
        [DllImport("iphlpapi.dll")] public static extern int GetIfEntry2(IntPtr row);
        [DllImport("iphlpapi.dll")] public static extern void FreeMibTable(IntPtr table);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll")] public static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
    }
}
