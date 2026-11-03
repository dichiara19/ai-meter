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
            : this(slot == null ? d.Id : d.Id + "." + slot, d.Name, slot == null ? null : Json.Obj(Json.Obj(d.Raw, "keys"), slot), slot, d.KeyPage) { }

        /// <summary>An extra key of a service implemented in code, described like a definition's "keys" entry.</summary>
        public ApiKeyWindow(string keyId, string serviceName, string label, string description, string keyPage)
            : this(keyId, serviceName, new Dictionary<string, object> { { "label", label }, { "description", description }, { "page", keyPage } }, null, keyPage) { }

        private ApiKeyWindow(string keyId, string serviceName, Dictionary<string, object> extra, string slot, string mainKeyPage)
        {
            string keyPage = extra != null ? Json.Str(extra, "page") : mainKeyPage;
            Title = extra != null ? L.T(Json.Str(extra, "label") ?? slot) + " · " + serviceName : L.F("Connect {0}", serviceName);
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
            // An extra value that is not a secret, such as an account ID, is typed in plain sight and described on its own.
            bool secret = !(extra != null && extra.ContainsKey("secret") && !Json.Bool(extra, "secret"));
            string description = extra != null && Json.Str(extra, "description") != null ? L.T(Json.Str(extra, "description")) : null;
            var info = Ui.Text(!secret ? description
                : (description != null ? description + " " : "")
                  + L.F("Paste an API key of your {0} account. AI Meter uses it only to read your usage; it is encrypted with Windows data protection and stays on this PC.", serviceName), 13, "Text2");
            info.TextWrapping = TextWrapping.Wrap;
            info.TextTrimming = TextTrimming.None;
            info.Margin = new Thickness(0, 8, 0, 16);
            root.Children.Add(info);

            Control box;
            Func<string> entered;
            if (secret)
            {
                var hidden = new PasswordBox();
                hidden.SetResourceReference(PasswordBox.CaretBrushProperty, "Text");
                box = hidden;
                entered = () => hidden.Password;
            }
            else
            {
                var plain = new TextBox();
                plain.SetResourceReference(TextBox.CaretBrushProperty, "Text");
                box = plain;
                entered = () => plain.Text;
            }
            box.BorderThickness = new Thickness(0);
            box.Background = System.Windows.Media.Brushes.Transparent;
            box.FontSize = 14;
            box.Padding = new Thickness(0);
            box.SetResourceReference(Control.ForegroundProperty, "Text");
            var frame = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(10, 6, 10, 6), Child = box };
            frame.SetResourceReference(Border.BackgroundProperty, "Control");
            frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            box.GotKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            box.LostKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            root.Children.Add(frame);
            // A value with a "pattern" is saved as the part that matches it, such as the ID in a pasted address.
            var wrong = Ui.Text(L.T("Not recognised: check what you pasted."), 12, "Text3");
            wrong.Margin = new Thickness(2, 6, 0, 0);
            wrong.Visibility = Visibility.Collapsed;
            root.Children.Add(wrong);

            var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            if (keyPage != null)
            {
                // An extra value names its own page, such as the dashboard that shows an account ID, with "pageLabel".
                string linkText = extra != null && Json.Str(extra, "pageLabel") != null ? L.T(Json.Str(extra, "pageLabel")) : L.T("Create a key");
                var link = new Button { Content = linkText, Padding = new Thickness(0, 0, 4, 1), FontSize = 13 };
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
            Func<string> value = () => extra != null && Json.Str(extra, "pattern") != null ? ServiceDefinition.SlotText(extra, entered()) : entered().Trim();
            save.Click += (o, e) => { ApiKeys.Save(keyId, value()); if (Saved != null) Saved(); Close(); };
            DockPanel.SetDock(save, Dock.Right);
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            Action check = () =>
            {
                string v = value();
                save.IsEnabled = v != null && v.Length >= 8;
                wrong.Visibility = v == null && entered().Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            if (box is PasswordBox) ((PasswordBox)box).PasswordChanged += (o, e) => check();
            else ((TextBox)box).TextChanged += (o, e) => check();
            root.Children.Add(buttons);
            Content = root;
            Loaded += (o, e) => box.Focus();
            // Back from the page with the value copied, such as the dashboard's address with the account ID in it, the
            // empty box takes it from the clipboard; only for a value that is not a secret and matches its pattern.
            if (!secret && extra != null && Json.Str(extra, "pattern") != null)
                Activated += (o, e) =>
                {
                    var plain = (TextBox)box;
                    if (plain.Text.Length > 0) return;
                    string copied = null;
                    try { if (Clipboard.ContainsText()) copied = Clipboard.GetText(); }
                    catch (COMException) { }
                    if (copied != null && copied.Length < 2048 && ServiceDefinition.SlotText(extra, copied) != null) plain.Text = copied.Trim();
                };
        }
    }
}
