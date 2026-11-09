using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace AIMeter
{
    /// <summary>
    /// Microsoft Store screenshots: 16:9 slides at 3840x2160, each a localised headline beside a scene built from the
    /// real interface with demo data (the same pieces as MediaRenderer). Run tools\render-store.ps1.
    /// </summary>
    /// <remarks>
    /// Every scene is rendered to a bitmap as soon as it is built, under the theme it needs, because the controls follow
    /// the application's theme resources: a light panel built beside a dark one would turn dark when the theme changes.
    /// </remarks>
    public static class StoreScreenshots
    {
        private const int SlideW = 1920, SlideH = 1080, Scale = 2;
        private const double TextLeft = 104, TextWidth = 590, StageLeft = 744, StageWidth = 1080, StageHeight = 888;
        private static readonly Color Orange = Color.FromRgb(0xF0, 0x6A, 0x3B), Green = Color.FromRgb(0x2F, 0xC2, 0x8A);

        /// <summary>Headline and subline of each slide, then the note on slides that show invented data, per language.</summary>
        private static readonly Dictionary<string, string[]> Copy = new Dictionary<string, string[]>
        {
            { "en", new[] {
                "Your AI limits, right in the taskbar", "Hover for the summary, click for everything: Claude, ChatGPT, Copilot, Antigravity and more.",
                "Know before you hit the wall", "A notification at 20% and 5% tells you when a limit runs out and which service still has room.",
                "Every limit in one panel", "5-hour and weekly limits, reset times and a forecast at your current pace.",
                "Local models in plain sight", "Video memory, context in use and which coding agent is talking to which model.",
                "Your usage over time, on your PC", "Quota cycles, daily activity, models and tools. No telemetry, no account.",
                "Looks like Windows made it", "Fluent design with Mica and Acrylic, light and dark mode, six languages.",
                "Sample data" } },
            { "it", new[] {
                "I limiti delle tue IA, nella barra delle applicazioni", "Passa sopra per il riepilogo, clicca per tutto il resto: Claude, ChatGPT, Copilot, Antigravity e altri.",
                "Sai prima quando stai per finire", "Una notifica al 20% e al 5% ti dice quando finisce un limite e quale servizio ha ancora spazio.",
                "Tutti i limiti in un pannello", "Limiti di 5 ore e settimanali, orari di ripristino e una previsione al ritmo attuale.",
                "I modelli locali, sempre in vista", "Memoria video, contesto in uso e quale agente di programmazione sta usando quale modello.",
                "Il tuo utilizzo nel tempo, sul tuo PC", "Cicli di quota, attività giornaliera, modelli e strumenti. Niente telemetria, nessun account.",
                "Sembra parte di Windows", "Design Fluent con Mica e Acrylic, tema chiaro e scuro, sei lingue.",
                "Dati di esempio" } },
            { "es", new[] {
                "Tus límites de IA, en la barra de tareas", "Pasa el ratón para ver el resumen y haz clic para verlo todo: Claude, ChatGPT, Copilot, Antigravity y más.",
                "Entérate antes de quedarte sin cuota", "Una notificación al 20 % y al 5 % te avisa de cuándo se agota un límite y qué servicio aún tiene margen.",
                "Todos los límites en un panel", "Límites de 5 horas y semanales, horas de reinicio y una previsión a tu ritmo actual.",
                "Tus modelos locales, siempre a la vista", "Memoria de vídeo, contexto en uso y qué agente de programación usa cada modelo.",
                "Tu uso a lo largo del tiempo, en tu PC", "Ciclos de cuota, actividad diaria, modelos y herramientas. Sin telemetría ni cuentas.",
                "Parece parte de Windows", "Diseño Fluent con Mica y Acrylic, modo claro y oscuro, seis idiomas.",
                "Datos de ejemplo" } },
            { "fr", new[] {
                "Vos limites d'IA, dans la barre des tâches", "Survolez pour le résumé, cliquez pour tout voir : Claude, ChatGPT, Copilot, Antigravity et plus.",
                "Soyez prévenu avant d'atteindre la limite", "Une notification à 20 % et à 5 % indique quand une limite s'épuise et quel service a encore de la marge.",
                "Toutes vos limites dans un panneau", "Limites sur 5 heures et à la semaine, heures de réinitialisation et prévision au rythme actuel.",
                "Vos modèles locaux, toujours visibles", "Mémoire vidéo, contexte utilisé et quel agent de code utilise quel modèle.",
                "Votre utilisation dans le temps, sur votre PC", "Cycles de quota, activité quotidienne, modèles et outils. Sans télémétrie ni compte.",
                "On dirait une partie de Windows", "Design Fluent avec Mica et Acrylic, mode clair et sombre, six langues.",
                "Données d'exemple" } },
            { "de", new[] {
                "Ihre KI-Limits, direkt in der Taskleiste", "Darauf zeigen für die Übersicht, klicken für alle Details: Claude, ChatGPT, Copilot, Antigravity und mehr.",
                "Wissen, bevor das Limit erreicht ist", "Eine Benachrichtigung bei 20 % und 5 % zeigt, wann ein Limit ausgeschöpft ist und welcher Dienst noch Spielraum hat.",
                "Alle Limits in einem Panel", "5-Stunden- und Wochenlimits, Zeitpunkte des Zurücksetzens und eine Prognose bei Ihrem aktuellen Tempo.",
                "Lokale Modelle immer im Blick", "Grafikspeicher, genutzter Kontext und welcher Coding-Agent mit welchem Modell arbeitet.",
                "Ihre Nutzung im Zeitverlauf, auf Ihrem PC", "Kontingentzyklen, tägliche Aktivität, Modelle und Tools. Keine Telemetrie, kein Konto.",
                "Wirkt wie ein Teil von Windows", "Fluent Design mit Mica und Acrylic, heller und dunkler Modus, sechs Sprachen.",
                "Beispieldaten" } },
            { "pt", new[] {
                "Seus limites de IA, na barra de tarefas", "Passe o mouse para ver o resumo e clique para ver tudo: Claude, ChatGPT, Copilot, Antigravity e mais.",
                "Saiba antes de chegar ao limite", "Uma notificação em 20% e 5% avisa quando um limite vai acabar e qual serviço ainda tem espaço.",
                "Todos os limites em um painel", "Limites de 5 horas e semanais, horários de redefinição e uma previsão no seu ritmo atual.",
                "Seus modelos locais sempre à vista", "Memória de vídeo, contexto em uso e qual agente de programação está usando qual modelo.",
                "Seu uso ao longo do tempo, no seu PC", "Ciclos de cota, atividade diária, modelos e ferramentas. Sem telemetria, sem conta.",
                "Parece parte do Windows", "Design Fluent com Mica e Acrylic, modo claro e escuro, seis idiomas.",
                "Dados de exemplo" } },
        };

        /// <summary>
        /// Write six slides per language to <paramref name="outDir"/>\&lt;language&gt;. <paramref name="historyDir"/> holds,
        /// per language, the history-dark-release.png that tools\render-history.ps1 wrote for that language.
        /// </summary>
        public static string Run(string outDir, string historyDir, string[] languages)
        {
            int count = 0;
            foreach (string lang in languages)
            {
                string[] copy = Copy[lang];
                MediaRenderer.Prepare(lang);
                // Relative reset times depend on the clock, so use the current minute like MediaRenderer.
                DateTime now = DateTime.Now;
                now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
                var snap = DemoData.Build(now);
                string dir = IOPath.Combine(outDir, lang);
                Directory.CreateDirectory(dir);

                var scenes = new[]
                {
                    TaskbarScene(snap), ToastScene(snap), PanelScene(snap, snap, 1.3), PanelScene(snap, LocalOnly(snap), 1.8),
                    HistoryScene(IOPath.Combine(historyDir, lang, "history-dark-release.png")), ThemesScene(snap),
                };
                string[] names = { "1-taskbar", "2-notification", "3-panel", "4-local-models", "5-history", "6-light-and-dark" };
                for (int i = 0; i < scenes.Length; i++)
                {
                    var slide = Slide(copy[2 * i], copy[2 * i + 1], scenes[i], i == 5 ? null : copy[12]);
                    MediaRenderer.Save(slide, IOPath.Combine(dir, names[i] + ".png"), Scale);
                    count++;
                }
            }
            return "ok, " + count + " slides";
        }

        // Slide layout.
        /// <summary>A dark backdrop with the logo's colours, the app name, the headline and subline, and the scene on the right.</summary>
        private static Canvas Slide(string headline, string subline, FrameworkElement stage, string note)
        {
            var c = new Canvas { Width = SlideW, Height = SlideH, ClipToBounds = true };
            c.Background = new LinearGradientBrush(Color.FromRgb(0x0B, 0x0E, 0x18), Color.FromRgb(0x12, 0x17, 0x2B), 30);
            Glow(c, -260, -320, 1100, 1000, Orange, 0x55);
            Glow(c, 1100, 520, 1200, 900, Green, 0x40);

            var brand = new StackPanel { Orientation = Orientation.Horizontal };
            brand.Children.Add(new Image { Source = MediaRenderer.AppIcon(), Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) });
            var name = Text("AI Meter", 30, Theme.DisplayFont, FontWeights.SemiBold, 1);
            name.VerticalAlignment = VerticalAlignment.Center;
            brand.Children.Add(name);
            Canvas.SetLeft(brand, TextLeft); Canvas.SetTop(brand, 92);
            c.Children.Add(brand);

            var text = new StackPanel { Width = TextWidth };
            var head = Text(headline, 64, Theme.DisplayFont, FontWeights.SemiBold, 1); head.LineHeight = 74;
            text.Children.Add(head);
            var sub = Text(subline, 28, Theme.Font, FontWeights.Normal, 0.8); sub.LineHeight = 40; sub.Margin = new Thickness(0, 28, 0, 0);
            text.Children.Add(sub);
            text.Measure(new Size(TextWidth, double.PositiveInfinity));
            Canvas.SetLeft(text, TextLeft); Canvas.SetTop(text, Math.Max(180, (SlideH - text.DesiredSize.Height) / 2));
            c.Children.Add(text);

            if (note != null)
            {
                var n = Text(note, 18, Theme.Font, FontWeights.Normal, 0.5);
                Canvas.SetLeft(n, TextLeft); Canvas.SetTop(n, SlideH - 72);
                c.Children.Add(n);
            }

            stage.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(stage, StageLeft + (StageWidth - stage.Width) / 2);
            Canvas.SetTop(stage, (SlideH - stage.Height) / 2);
            c.Children.Add(stage);
            return c;
        }

        private static void Glow(Canvas c, double x, double y, double w, double h, Color col, byte alpha)
        {
            var e = new Ellipse { Width = w, Height = h, Fill = new RadialGradientBrush(Color.FromArgb(alpha, col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B)) };
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            c.Children.Add(e);
        }

        private static TextBlock Text(string t, double size, FontFamily font, FontWeight weight, double opacity)
        {
            return new TextBlock { Text = t, FontSize = size, FontFamily = font, FontWeight = weight, Foreground = Brushes.White, Opacity = opacity, TextWrapping = TextWrapping.Wrap };
        }

        /// <summary>
        /// Render a scene of <paramref name="w"/> by <paramref name="h"/> at <paramref name="zoom"/> times its size, at the
        /// slide's pixel density, and frame it like a screen with rounded corners and a shadow.
        /// </summary>
        private static FrameworkElement Stage(FrameworkElement scene, double w, double h, double zoom)
        {
            var image = new Image { Source = MediaRenderer.Render(scene, w, h, Scale * zoom), Width = w * zoom, Height = h * zoom, Stretch = Stretch.Fill };
            image.Clip = new RectangleGeometry(new Rect(0, 0, w * zoom, h * zoom), 18, 18);
            var frame = new Grid { Width = w * zoom, Height = h * zoom };
            frame.Children.Add(image);
            frame.Children.Add(new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) });
            frame.Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 18, Direction = 270, Opacity = 0.6, Color = Colors.Black };
            return frame;
        }

        /// <summary>The bottom-right corner of the desktop with the taskbar, as large as the stage at <paramref name="zoom"/>.</summary>
        private static Canvas Desk(Snapshot s, double zoom, out double w, out double h)
        {
            w = Math.Round(StageWidth / zoom); h = Math.Round(StageHeight / zoom);
            var c = new Canvas { Width = w, Height = h, ClipToBounds = true };
            var back = new Image { Source = new CroppedBitmap(MediaRenderer.wallpaper, new Int32Rect((int)(MediaRenderer.DeskW - w), (int)(MediaRenderer.DeskH - h), (int)w, (int)h)), Width = w, Height = h };
            c.Children.Add(back);
            var bar = MediaRenderer.Taskbar(s, w, false);
            Canvas.SetTop(bar, h - MediaRenderer.BarH);
            c.Children.Add(bar);
            return c;
        }

        // Scenes.
        private static FrameworkElement TaskbarScene(Snapshot s)
        {
            // Wide enough that the pill sits clear of the left edge, with its tooltip above it.
            const double zoom = 1.75;
            double w = Math.Round(StageWidth / zoom), h = Math.Round(StageHeight / zoom);
            return Stage(MediaRenderer.HeroTaskbar(s, w, h, true), w, h, zoom);
        }

        private static FrameworkElement ToastScene(Snapshot s)
        {
            double w, h;
            var desk = Desk(s, 2, out w, out h);
            var toast = MediaRenderer.Toast(s, new Rect(MediaRenderer.DeskW - 376, MediaRenderer.DeskH - MediaRenderer.BarH - 160, 364, 0));
            Canvas.SetLeft(toast, w - 364 - 12);
            Canvas.SetTop(toast, h - MediaRenderer.BarH - 12 - ((Grid)toast).Height);
            desk.Children.Add(toast);
            return Stage(desk, w, h, 2);
        }

        /// <summary>The demo snapshot with only the local models card, for the slide about them.</summary>
        private static Snapshot LocalOnly(Snapshot s)
        {
            var local = new Snapshot { Taken = s.Taken };
            foreach (var p in s.Providers) if (p.Id == "local") local.Providers.Add(p);
            return local;
        }

        /// <summary>
        /// The open flyout with the cards of <paramref name="cards"/> above a taskbar showing <paramref name="s"/>, like the
        /// real one: at most the work area's height, scrolling the cards that do not fit.
        /// </summary>
        private static FrameworkElement PanelScene(Snapshot s, Snapshot cards, double zoom)
        {
            double w, h;
            var desk = Desk(s, zoom, out w, out h);
            TranslateTransform scroll;
            double maxScroll;
            var panel = MediaRenderer.Panel(cards, true, new Rect(MediaRenderer.DeskW - 356, 12, 344, 0), h - MediaRenderer.BarH - 24, out scroll, out maxScroll);
            Canvas.SetLeft(panel, w - 344 - 12);
            Canvas.SetTop(panel, h - MediaRenderer.BarH - 12 - ((Grid)panel).Height);
            desk.Children.Add(panel);
            return Stage(desk, w, h, zoom);
        }

        /// <summary>The dashboard image from render-history.ps1 (2x), cropped to the window and the backdrop around it.</summary>
        private static FrameworkElement HistoryScene(string path)
        {
            var source = new BitmapImage();
            source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.UriSource = new Uri(path); source.EndInit();
            // The release scene is 1280x1000 at 2x with the 1160x860 window at (60, 60): keep the window and a thin margin,
            // leaving out the caption at the bottom.
            var crop = new CroppedBitmap(source, new Int32Rect(2 * 44, 2 * 44, 2 * 1192, 2 * 892));
            double w = 1192, h = 892, zoom = Math.Min(StageWidth / w, StageHeight / h);
            var image = new Image { Source = crop, Width = w * zoom, Height = h * zoom, Stretch = Stretch.Fill };
            image.Clip = new RectangleGeometry(new Rect(0, 0, w * zoom, h * zoom), 18, 18);
            var frame = new Grid { Width = w * zoom, Height = h * zoom };
            frame.Children.Add(image);
            frame.Children.Add(new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) });
            frame.Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 18, Direction = 270, Opacity = 0.6, Color = Colors.Black };
            return frame;
        }

        /// <summary>The same flyout in the dark and light theme, side by side, each rendered under its own theme.</summary>
        private static FrameworkElement ThemesScene(Snapshot s)
        {
            const double zoom = 1.15;
            double w = Math.Round(StageWidth / zoom), h = Math.Round(StageHeight / zoom), half = Math.Round(w / 2);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (bool dark in new[] { true, false })
            {
                MediaRenderer.SetTheme(dark);
                var c = new Canvas { Width = half, Height = h, ClipToBounds = true };
                double x = dark ? MediaRenderer.DeskW - 2 * half : MediaRenderer.DeskW - half;
                c.Children.Add(new Image { Source = new CroppedBitmap(MediaRenderer.wallpaper, new Int32Rect((int)x, 0, (int)half, (int)h)), Width = half, Height = h });
                TranslateTransform scroll;
                double maxScroll;
                var panel = MediaRenderer.Panel(s, dark, new Rect(x + (half - 344) / 2, 30, 344, 0), h - 60, out scroll, out maxScroll);
                Canvas.SetLeft(panel, (half - 344) / 2); Canvas.SetTop(panel, 30);
                c.Children.Add(panel);
                row.Children.Add(new Image { Source = MediaRenderer.Render(c, half, h, Scale * zoom), Width = half, Height = h });
            }
            MediaRenderer.SetTheme(true);
            return Stage(row, 2 * half, h, zoom);
        }
    }
}
