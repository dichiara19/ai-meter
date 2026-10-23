using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
        public bool Usable { get { return Horizontal && !AutoHide && Rect.Right > Rect.Left; } }
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
            if (!Horizontal) return "Rilevato: barra sul lato dello schermo. La pillola nella barra funziona solo con la barra in basso o in alto.";
            if (AutoHide) return "Rilevato: la barra si nasconde automaticamente. La pillola resterebbe sospesa da sola: meglio la sovrimpressione.";
            return string.Format("Rilevato: barra {0}, icone {1}, pulsante Widget {2}. Posizione consigliata: {3}.",
                Edge == 3 ? "in basso" : "in alto", Centered ? "al centro" : "a sinistra", WidgetsButton ? "visibile" : "nascosto",
                RecommendedSide == "left" ? "angolo sinistro" : "accanto all'area di notifica");
        }
    }

    /// <summary>
    /// Pillola agganciata alla barra delle applicazioni. Windows 11 non accetta elementi di terze parti dentro la barra,
    /// quindi è una finestra trasparente, sempre in primo piano, appoggiata esattamente sopra lo spazio libero.
    /// Usa la modalità colore di Windows (quella della barra), non quella delle app.
    /// </summary>
    internal sealed class TaskbarPill : Window
    {
        private readonly Controller C;
        private readonly Button chip = new Button { Padding = new Thickness(10, 0, 10, 0), Cursor = Cursors.Hand, Focusable = false };
        private readonly DispatcherTimer keepOnTop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private IntPtr hwnd;
        private uint taskbarCreated;
        public bool Dark { get; private set; }
        public bool OnLeft { get; private set; }
        public bool Usable { get; private set; }

        public TaskbarPill(Controller c)
        {
            C = c;
            Title = "AI Meter - barra delle applicazioni";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            // nasce fuori dallo schermo: se apparisse sotto il cursore e poi si spostasse, WPF la crederebbe ancora "sotto il mouse"
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000; Top = -10000;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            Cursor = Cursors.Hand;
            Content = chip;
            ApplySystemTheme();

            // un pulsante vero, con l'evidenziazione al passaggio come i pulsanti della barra
            chip.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
            System.Windows.Automation.AutomationProperties.SetName(chip, "AI Meter: apri il pannello");
            chip.Click += (o, e) => C.ToggleFlyoutFromTaskbar(this);
            MouseRightButtonUp += (o, e) => { C.ShowMenu(this); e.Handled = true; };

            SourceInitialized += (o, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, Native.WS_EX_APPWINDOW);
                taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
                HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            };
            SizeChanged += (o, e) => Place();
            // cliccando la barra Windows la riporta sopra di noi: ci rimettiamo davanti (costo trascurabile)
            keepOnTop.Tick += (o, e) => BringAboveTaskbar();
            IsVisibleChanged += (o, e) => { if (IsVisible) keepOnTop.Start(); else keepOnTop.Stop(); };
        }

        private IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (taskbarCreated != 0 && msg == (int)taskbarCreated)   // Esplora risorse riavviato: la barra è nuova
                Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Background);
            return IntPtr.Zero;
        }

        public void ApplySystemTheme()
        {
            Dark = Theme.TaskbarIsDark();
            var pal = Theme.BuildPalette(Dark);
            var hover = new SolidColorBrush(Dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0));
            hover.Freeze();
            pal["SubtleHover"] = hover;   // stessa evidenziazione dei pulsanti della barra
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(pal);
        }

        public void Render(Snapshot s, string metric)
        {
            chip.Content = Ui.PillRow(s, metric, Dark);
            ToolTip = Ui.Summary(s) + "\nClic per i dettagli · clic destro per le opzioni";
        }

        /// <summary>Posiziona la pillola nello spazio libero della barra del monitor principale.</summary>
        public void Place()
        {
            if (hwnd == IntPtr.Zero) return;
            var info = TaskbarInfo.Read();
            Usable = info.Usable;
            if (!Usable) return;
            var src = PresentationSource.FromVisual(this);
            double scale = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : 1;

            int barH = info.Rect.Bottom - info.Rect.Top;
            double chipH = Math.Max(24, Math.Floor(barH / scale) - 8);   // come i pulsanti della barra: 40 su 48
            if (double.IsNaN(chip.Height) || Math.Abs(chip.Height - chipH) > 0.5) { chip.Height = chipH; UpdateLayout(); }

            int w = (int)Math.Round(ActualWidth * scale), h = (int)Math.Round(ActualHeight * scale);
            string side = C.Settings.TaskbarSide == "left" || C.Settings.TaskbarSide == "right" ? C.Settings.TaskbarSide : info.RecommendedSide;
            OnLeft = side == "left";
            int gap = (int)Math.Round(8 * scale);
            int x;
            if (OnLeft) x = info.Rect.Left + (int)Math.Round(12 * scale);
            else if (info.TrayLeft > info.Rect.Left) x = info.TrayLeft - w - gap;
            else x = info.Rect.Right - w - (int)Math.Round(320 * scale);   // area di notifica non rilevata: stima prudente
            int y = info.Rect.Top + (barH - h) / 2;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Mouse.Synchronize();   // ricalcola subito lo stato "sotto il mouse" dopo lo spostamento
        }

        public void BringAboveTaskbar()
        {
            if (hwnd != IntPtr.Zero && IsVisible)
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Tema o barra cambiati (modalità di Windows, allineamento, pulsante Widget, risoluzione).</summary>
        public void Refresh(Snapshot s, string metric)
        {
            ApplySystemTheme();
            if (s != null) Render(s, metric);
            Place();
        }
    }
}
