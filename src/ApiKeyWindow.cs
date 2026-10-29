using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace AIMeter
{
    /// <summary>Prompt for a service API key and save it encrypted on this PC.</summary>
    internal sealed class ApiKeyWindow : Window
    {
        public event Action Saved;

        /// <summary>An optional slot saves an extra credential as &lt;id&gt;.&lt;slot&gt;; null selects the main key.</summary>
        public ApiKeyWindow(ServiceDefinition d, string slot)
        {
            var extra = slot == null ? null : Json.Obj(Json.Obj(d.Raw, "keys"), slot);
            string keyPage = extra != null ? Json.Str(extra, "page") : d.KeyPage;
            Title = extra != null ? L.T(Json.Str(extra, "label") ?? slot) + " · " + d.Name : L.F("Connect {0}", d.Name);
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            Icon = Controller.AppIconImage();
            Program.Brand(this);
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 32, GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = true, ResizeBorderThickness = new Thickness(0) });
            SourceInitialized += (o, e) => Ui.ApplyBackdrop(this, 2, false);

            var root = new StackPanel { Margin = new Thickness(24, 40, 24, 24) };
            var h = Ui.Text(Title, 20, "Text", FontWeights.SemiBold);
            h.FontFamily = Theme.DisplayFont;
            root.Children.Add(h);
            var info = Ui.Text((extra != null && Json.Str(extra, "description") != null ? L.T(Json.Str(extra, "description")) + " " : "")
                + L.F("Paste an API key of your {0} account. AI Meter uses it only to read your usage; it is encrypted with Windows data protection and stays on this PC.", d.Name), 13, "Text2");
            info.TextWrapping = TextWrapping.Wrap;
            info.TextTrimming = TextTrimming.None;
            info.Margin = new Thickness(0, 8, 0, 16);
            root.Children.Add(info);

            var box = new PasswordBox { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, FontSize = 14, Padding = new Thickness(0) };
            box.SetResourceReference(Control.ForegroundProperty, "Text");
            box.SetResourceReference(PasswordBox.CaretBrushProperty, "Text");
            var frame = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(10, 6, 10, 6), Child = box };
            frame.SetResourceReference(Border.BackgroundProperty, "Control");
            frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            box.GotKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            box.LostKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            root.Children.Add(frame);

            var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            if (keyPage != null)
            {
                var link = new Button { Content = L.T("Create a key"), Padding = new Thickness(0, 0, 4, 1), FontSize = 13 };
                link.SetResourceReference(StyleProperty, "LinkButton");
                link.Click += (o, e) => { try { System.Diagnostics.Process.Start(keyPage); } catch (Exception) { } };
                DockPanel.SetDock(link, Dock.Left);
                buttons.Children.Add(link);
            }
            var cancel = new Button { Content = L.T("Cancel") };
            cancel.SetResourceReference(StyleProperty, "StdButton");
            cancel.Click += (o, e) => Close();
            DockPanel.SetDock(cancel, Dock.Right);
            var save = new Button { Content = L.T("Save"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, IsDefault = true };
            save.SetResourceReference(StyleProperty, "AccentButton");
            save.Click += (o, e) => { ApiKeys.Save(slot == null ? d.Id : d.Id + "." + slot, box.Password); if (Saved != null) Saved(); Close(); };
            DockPanel.SetDock(save, Dock.Right);
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            box.PasswordChanged += (o, e) => save.IsEnabled = box.Password.Trim().Length >= 8;
            root.Children.Add(buttons);
            Content = root;
            Loaded += (o, e) => box.Focus();
        }
    }
}
