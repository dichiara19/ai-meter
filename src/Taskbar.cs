using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AIMeter
{
    /// <summary>Come è configurata la barra delle applicazioni del monitor principale.</summary>
    internal sealed class TaskbarInfo
    {
        public Native.RECT Rect;     // pixel fisici
        public int Edge;             // 0 sinistra, 1 alto, 2 destra, 3 basso
        public bool Centered;        // icone al centro (impostazione predefinita di Windows 11)
        public bool WidgetsButton;   // pulsante Widget (meteo) nell'angolo sinistro
        public bool AutoHide;
        public int TrayLeft;         // bordo sinistro dell'area di notifica, pixel fisici (0 se ignoto)

        public bool Horizontal { get { return Edge == 1 || Edge == 3; } }
        public bool Usable { get { return Horizontal && Rect.Right > Rect.Left; } }
        /// <summary>Lato consigliato: l'angolo sinistro è libero solo con icone al centro e senza pulsante Widget.</summary>
        public string RecommendedSide { get { return Centered && !WidgetsButton ? "left" : "right"; } }

        public static TaskbarInfo Read()
        {
            var t = new TaskbarInfo();
            Native.TaskbarPos(out t.Rect, out t.Edge, out t.AutoHide);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    object al = k == null ? null : k.GetValue("TaskbarAl");
                    object da = k == null ? null : k.GetValue("TaskbarDa");
                    t.Centered = al == null || Convert.ToInt32(al) != 0;      // assente = al centro
                    t.WidgetsButton = da == null || Convert.ToInt32(da) != 0; // assente = visibile
                }
            }
            catch (Exception) { t.Centered = true; t.WidgetsButton = true; }
            t.TrayLeft = Native.TrayLeft();
            return t;
        }

        /// <summary>Descrizione leggibile per le Impostazioni.</summary>
        public string Describe()
        {
            if (!Horizontal) return L.T("Detected: taskbar on the side of the screen. The taskbar pill works only with the taskbar at the bottom or top.");

            return L.F("Detected: taskbar {0}, icons {1}, Widgets button {2}. Recommended position: {3}.",
                L.T(Edge == 3 ? "at the bottom" : "at the top"), L.T(Centered ? "centered" : "on the left"), L.T(WidgetsButton ? "visible" : "hidden"),
                L.T(RecommendedSide == "left" ? "left corner" : "next to the notification area"));
        }
    }

    /// <summary>
    /// Pillola dentro la barra delle applicazioni. Windows 11 non ha più le "deskband", quindi la pillola diventa una
    /// finestra figlia della barra (Shell_TrayWnd): sale e scende con la barra a scomparsa, sparisce con essa a schermo
    /// intero e non finisce mai sotto la barra quando la si clicca. Se Esplora risorse si riavvia, viene ricreata.
    /// Usa la modalità colore di Windows (quella della barra), non quella delle app.
    /// </summary>
    internal sealed class TaskbarPill
    {
        private readonly Controller C;
        private readonly Border root = new Border { Background = Brushes.Transparent };
        private readonly Button chip = new Button { Padding = new Thickness(10, 0, 10, 0), Cursor = Cursors.Hand, Focusable = false };
        private readonly DispatcherTimer reconcile = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private HwndSource source;
        private IntPtr bar;
        private Native.RECT lastRect;
        private string placedKey;      // barra, area di notifica e lato all'ultimo posizionamento
        private int px, py, pw, ph;    // ultima posizione applicata
        private bool visible = true;
        public bool Dark { get; private set; }
        public bool OnLeft { get; private set; }
        public bool Usable { get; private set; }
        public bool IsVisible { get { return visible && source != null && Usable; } }

        public TaskbarPill(Controller c)
        {
            C = c;
            root.Child = chip;
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
            ApplySystemTheme();
            // un pulsante vero, con l'evidenziazione al passaggio come i pulsanti della barra
            chip.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
            chip.FontFamily = Theme.Font;
            AutomationProperties.SetName(chip, L.T("AI Meter: open the panel"));
            chip.Click += (o, e) => C.ToggleFlyoutFromTaskbar(this);
            chip.MouseRightButtonUp += (o, e) => { C.ShowMenu(null); e.Handled = true; };
            // controllo leggero: barra ricreata (Esplora risorse riavviato), spostata o ridimensionata
            reconcile.Tick += (o, e) => Reconcile();
        }

        public void Show() { visible = true; Reconcile(); reconcile.Start(); }

        public void Hide()
        {
            visible = false;
            reconcile.Stop();
            if (source != null) Native.ShowWindow(source.Handle, 0);
        }

        public void Close()
        {
            reconcile.Stop();
            if (source != null) { source.RootVisual = null; source.Dispose(); source = null; }
        }

        public void ApplySystemTheme()
        {
            Dark = Theme.TaskbarIsDark();
            var pal = Theme.BuildPalette(Dark);
            var hover = new SolidColorBrush(Dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0));
            hover.Freeze();
            pal["SubtleHover"] = hover;   // stessa evidenziazione dei pulsanti della barra
            root.Resources.MergedDictionaries.Clear();
            root.Resources.MergedDictionaries.Add(pal);
        }

        public void Render(Snapshot s, string metric)
        {
            chip.Content = Ui.PillRow(s, metric, Dark);
            chip.ToolTip = Ui.Summary(s) + "\n" + L.T("Click for details · right-click for options");
            Place();
        }

        /// <summary>Crea la finestra figlia dentro la barra attuale (anche dopo un riavvio di Esplora risorse).</summary>
        private void Attach()
        {
            Close();
            bar = Native.FindTaskbar();
            if (bar == IntPtr.Zero) return;
            var prm = new HwndSourceParameters("AI Meter - taskbar")
            {
                ParentWindow = bar,
                WindowStyle = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS,
                UsesPerPixelTransparency = true,   // finestra figlia "layered": sfondo trasparente sopra la barra
                PositionX = 0, PositionY = 0,
            };
            // la barra può rifiutare la finestra figlia mentre viene ricreata (accesso, risveglio, riavvio di Esplora
            // risorse): "Accesso negato". Si riprova al controllo successivo, tra 2 secondi, senza chiudere l'app.
            try { source = new HwndSource(prm) { SizeToContent = SizeToContent.WidthAndHeight, RootVisual = root }; }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Program.LogError("taskbar pill", ex);
                source = null;
                bar = IntPtr.Zero;
                return;
            }
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            source.SizeToContentChanged += (o, e) => Place();
            lastRect = default(Native.RECT);
        }

        private void Reconcile()
        {
            if (!visible) return;
            if (source == null || bar == IntPtr.Zero || !Native.IsWindow(bar) || bar != Native.FindTaskbar()) { Attach(); placedKey = null; }
            // controllo da pochi microsecondi: la pillola si riposiziona solo se barra o area di notifica sono cambiate
            if (Key() != placedKey) Place();
        }

        private string Key() { return Native.RectKey(bar) + "|" + Native.TrayLeft() + "|" + C.Settings.TaskbarSide; }

        /// <summary>Posiziona la pillola nello spazio libero della barra del monitor principale.</summary>
        public void Place()
        {
            if (source == null || !visible) return;
            var info = TaskbarInfo.Read();
            Usable = info.Usable;
            if (!Usable) { Native.ShowWindow(source.Handle, 0); placedKey = null; return; }
            double scale = source.CompositionTarget.TransformToDevice.M11;

            int barH = info.Rect.Bottom - info.Rect.Top;
            double chipH = Math.Max(24, Math.Floor(barH / scale) - 8);   // come i pulsanti della barra: 40 su 48
            if (double.IsNaN(chip.Height) || Math.Abs(chip.Height - chipH) > 0.5) { chip.Height = chipH; root.UpdateLayout(); }

            int w = (int)Math.Round(root.ActualWidth * scale), h = (int)Math.Round(root.ActualHeight * scale);
            string side = C.Settings.TaskbarSide == "left" || C.Settings.TaskbarSide == "right" ? C.Settings.TaskbarSide : info.RecommendedSide;
            OnLeft = side == "left";
            int gap = (int)Math.Round(8 * scale);
            int x;
            if (OnLeft) x = (int)Math.Round(12 * scale);
            else if (info.TrayLeft > info.Rect.Left) x = info.TrayLeft - info.Rect.Left - w - gap;
            else x = info.Rect.Right - info.Rect.Left - w - (int)Math.Round(320 * scale);   // area di notifica non rilevata: stima prudente
            int y = (barH - h) / 2;
            // coordinate relative alla barra; in cima alle altre finestre figlie della barra
            if (x != px || y != py || w != pw || h != ph || placedKey == null)
            {
                Native.SetWindowPos(source.Handle, IntPtr.Zero, x, y, w, h, Native.SWP_NOACTIVATE);
                Native.ShowWindow(source.Handle, 4 /* SW_SHOWNOACTIVATE */);
                px = x; py = y; pw = w; ph = h;
            }
            lastRect = info.Rect;
            placedKey = Key();
        }

        /// <summary>Tema o barra cambiati (modalità di Windows, allineamento, pulsante Widget, risoluzione).</summary>
        public void Refresh(Snapshot s, string metric)
        {
            ApplySystemTheme();
            placedKey = null;
            if (s != null) Render(s, metric);
            Reconcile();
        }
    }
}