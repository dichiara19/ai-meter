using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace AIMeter
{
    /// <summary>Colori e stili ricalcati sui token Fluent di Windows 11.</summary>
    internal static class Theme
    {
        public static bool Dark { get; private set; }
        private static ResourceDictionary palette;
        private static ResourceDictionary styles;

        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily DisplayFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static void Apply(Settings s)
        {
            Dark = s.Theme == "dark" || (s.Theme == "system" && SystemIsDark());
            var res = Application.Current.Resources;
            if (palette != null) res.MergedDictionaries.Remove(palette);
            palette = BuildPalette(Dark);
            res.MergedDictionaries.Insert(0, palette);
            if (styles == null)
            {
                styles = (ResourceDictionary)XamlReader.Parse(StylesXaml);
                res.MergedDictionaries.Add(styles);
            }
        }

        /// <summary>
        /// Tema della barra delle applicazioni: segue la "modalità di Windows" (SystemUsesLightTheme),
        /// che può differire dalla modalità delle app.
        /// </summary>
        public static bool TaskbarIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k == null || !Equals(k.GetValue("SystemUsesLightTheme"), 1);
            }
            catch (Exception) { return true; }
        }
        public static bool SystemIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Equals(k.GetValue("AppsUseLightTheme"), 0);
            }
            catch (Exception) { return false; }
        }

        public static Brush B(string key) { return (Brush)Application.Current.Resources[key]; }

        private static Color C(uint argb) { return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb); }

        public static ResourceDictionary BuildPalette(bool dark)
        {
            var d = new ResourceDictionary();
            Action<string, uint> add = (k, v) => { var b = new SolidColorBrush(C(v)); b.Freeze(); d[k] = b; };
            Color accentFill, accentText;
            SystemAccent(dark, out accentFill, out accentText);
            var af = new SolidColorBrush(accentFill); af.Freeze(); d["Accent"] = af;
            var at = new SolidColorBrush(accentText); at.Freeze(); d["AccentText"] = at;

            if (dark)
            {
                add("Text", 0xFFFFFFFF); add("Text2", 0xC5FFFFFF); add("Text3", 0x87FFFFFF); add("TextDisabled", 0x5DFFFFFF);
                add("OnAccent", 0xFF000000);
                add("Card", 0x0DFFFFFF); add("CardStroke", 0x12FFFFFF); add("CardHover", 0x15FFFFFF);
                add("Control", 0x0FFFFFFF); add("ControlHover", 0x15FFFFFF); add("ControlPressed", 0x08FFFFFF);
                add("ControlStroke", 0x12FFFFFF); add("ControlStrong", 0x8BFFFFFF);
                add("Subtle", 0x00FFFFFF); add("SubtleHover", 0x0FFFFFFF); add("SubtlePressed", 0x0AFFFFFF);
                add("Divider", 0x15FFFFFF); add("Track", 0x28FFFFFF);
                add("Fallback", 0xFF202020); add("Layer", 0x4C3A3A3A);
                add("Flyout", 0xFF2C2C2C); add("FlyoutStroke", 0x33000000);
                add("Caution", 0xFFFCE100); add("Critical", 0xFFFF99A4); add("Success", 0xFF6CCB5F); add("Info", 0xFF60CDFF);
                add("Claude", 0xFFE08A6A); add("OpenAI", 0xFF3DD68C); add("Copilot", 0xFFC297FF);
            }
            else
            {
                add("Text", 0xE4000000); add("Text2", 0x9E000000); add("Text3", 0x72000000); add("TextDisabled", 0x5C000000);
                add("OnAccent", 0xFFFFFFFF);
                add("Card", 0xB3FFFFFF); add("CardStroke", 0x0F000000); add("CardHover", 0xD9FFFFFF);
                add("Control", 0xB3FFFFFF); add("ControlHover", 0x80F9F9F9); add("ControlPressed", 0x4DF9F9F9);
                add("ControlStroke", 0x29000000); add("ControlStrong", 0x72000000);
                add("Subtle", 0x00000000); add("SubtleHover", 0x09000000); add("SubtlePressed", 0x06000000);
                add("Divider", 0x0F000000); add("Track", 0x1F000000);
                add("Fallback", 0xFFF3F3F3); add("Layer", 0x80FFFFFF);
                add("Flyout", 0xFFF9F9F9); add("FlyoutStroke", 0x0F000000);
                add("Caution", 0xFF9D5D00); add("Critical", 0xFFC42B1C); add("Success", 0xFF0F7B0F); add("Info", 0xFF005FB8);
                add("Claude", 0xFFC2603D); add("OpenAI", 0xFF0E8A5F); add("Copilot", 0xFF8250DF);
            }
            return d;
        }

        /// <summary>Colore d'accento di Windows, nella variante usata dai controlli Fluent.</summary>
        private static void SystemAccent(bool dark, out Color fill, out Color text)
        {
            fill = dark ? C(0xFF60CDFF) : C(0xFF005FB8);
            text = fill;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var pal = k == null ? null : k.GetValue("AccentPalette") as byte[];
                    if (pal != null && pal.Length >= 32)
                    {
                        // 0 Light3, 1 Light2, 2 Light1, 3 Base, 4 Dark1, 5 Dark2, 6 Dark3
                        int i = dark ? 1 : 4;
                        fill = Color.FromRgb(pal[i * 4], pal[i * 4 + 1], pal[i * 4 + 2]);
                        int j = dark ? 0 : 5;
                        text = Color.FromRgb(pal[j * 4], pal[j * 4 + 1], pal[j * 4 + 2]);
                    }
                }
            }
            catch (Exception) { }
        }

        private const string StylesXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <Style x:Key='FocusNone'><Setter Property='Control.Template'><Setter.Value><ControlTemplate/></Setter.Value></Setter></Style>

  <!-- pulsante icona (barra del titolo dei widget) -->
  <Style x:Key='IconButton' TargetType='Button'>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Foreground' Value='{DynamicResource Text2}'/>
    <Setter Property='Width' Value='28'/><Setter Property='Height' Value='28'/>
    <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Cursor' Value='Arrow'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='bd' Background='{DynamicResource Subtle}' CornerRadius='4'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource SubtleHover}'/><Setter Property='Foreground' Value='{DynamicResource Text}'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource SubtlePressed}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <!-- pulsante standard Fluent -->
  <Style x:Key='StdButton' TargetType='Button'>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='MinHeight' Value='32'/>
    <Setter Property='Padding' Value='12,0'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='bd' Background='{DynamicResource Control}' BorderBrush='{DynamicResource ControlStroke}' BorderThickness='1' CornerRadius='4' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource ControlHover}'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource ControlPressed}'/><Setter Property='Foreground' Value='{DynamicResource Text2}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='AccentButton' TargetType='Button' BasedOn='{StaticResource StdButton}'>
    <Setter Property='Foreground' Value='{DynamicResource OnAccent}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='bd' Background='{DynamicResource Accent}' CornerRadius='4' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.9'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.8'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <!-- link testuale (HyperlinkButton) -->
  <Style x:Key='LinkButton' TargetType='Button'>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Foreground' Value='{DynamicResource AccentText}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='bd' Background='Transparent' CornerRadius='4' Padding='{TemplateBinding Padding}'>
          <ContentPresenter VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource SubtleHover}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <!-- interruttore Fluent (ToggleSwitch) su CheckBox -->
  <Style x:Key='ToggleSwitch' TargetType='CheckBox'>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='CheckBox'>
        <StackPanel Orientation='Horizontal' Background='Transparent'>
          <TextBlock x:Name='lbl' Text='Disattivato' FontSize='14' VerticalAlignment='Center' Margin='0,0,12,0' MinWidth='70' TextAlignment='Right' Foreground='{DynamicResource Text}'/>
          <Grid Width='40' Height='20'>
            <Border x:Name='track' CornerRadius='10' BorderThickness='1' BorderBrush='{DynamicResource ControlStrong}' Background='{DynamicResource Subtle}'/>
            <Ellipse x:Name='knob' Width='12' Height='12' HorizontalAlignment='Left' Margin='4,0,0,0' Fill='{DynamicResource Text2}'/>
          </Grid>
        </StackPanel>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='track' Property='Background' Value='{DynamicResource SubtleHover}'/>
            <Setter TargetName='knob' Property='Width' Value='14'/><Setter TargetName='knob' Property='Height' Value='14'/>
            <Setter TargetName='knob' Property='Margin' Value='3,0,0,0'/>
          </Trigger>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='lbl' Property='Text' Value='Attivato'/>
            <Setter TargetName='track' Property='Background' Value='{DynamicResource Accent}'/>
            <Setter TargetName='track' Property='BorderBrush' Value='{DynamicResource Accent}'/>
            <Setter TargetName='knob' Property='Fill' Value='{DynamicResource OnAccent}'/>
            <Setter TargetName='knob' Property='HorizontalAlignment' Value='Right'/>
            <Setter TargetName='knob' Property='Margin' Value='0,0,4,0'/>
          </Trigger>
          <MultiTrigger>
            <MultiTrigger.Conditions><Condition Property='IsChecked' Value='True'/><Condition Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions>
            <Setter TargetName='knob' Property='Margin' Value='0,0,3,0'/>
          </MultiTrigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <!-- ComboBox Fluent -->
  <Style x:Key='ComboToggle' TargetType='ToggleButton'>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ToggleButton'>
        <Border x:Name='bd' Background='{DynamicResource Control}' BorderBrush='{DynamicResource ControlStroke}' BorderThickness='1' CornerRadius='4'>
          <TextBlock Text='&#xE70D;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='12' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,11,0' Foreground='{DynamicResource Text2}'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource ControlHover}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Combo' TargetType='ComboBox'>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='MinWidth' Value='200'/>
    <Setter Property='Height' Value='32'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ComboBox'>
        <Grid>
          <ToggleButton Style='{StaticResource ComboToggle}' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}' ClickMode='Press'/>
          <ContentPresenter IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' Margin='12,0,36,0' VerticalAlignment='Center' HorizontalAlignment='Left'/>
          <Popup IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' PopupAnimation='Fade' Focusable='False'>
            <Border Background='{DynamicResource Flyout}' BorderBrush='{DynamicResource FlyoutStroke}' BorderThickness='1' CornerRadius='8' Padding='2,4' Margin='0,4,0,8'
                    MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'>
              <Border.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='4' Opacity='0.28'/></Border.Effect>
              <ItemsPresenter/>
            </Border>
          </Popup>
        </Grid>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='ComboBoxItem'>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ComboBoxItem'>
        <Border x:Name='bd' Background='Transparent' CornerRadius='4' Margin='4,1' Height='34'>
          <Grid>
            <Border x:Name='pill' Width='3' Height='16' CornerRadius='1.5' Background='{DynamicResource Accent}' HorizontalAlignment='Left' Visibility='Collapsed'/>
            <ContentPresenter Margin='12,0' VerticalAlignment='Center'/>
          </Grid>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource SubtleHover}'/></Trigger>
          <Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource SubtleHover}'/><Setter TargetName='pill' Property='Visibility' Value='Visible'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <!-- menu contestuale Fluent -->
  <Style TargetType='ContextMenu'>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ContextMenu'>
        <Border Background='{DynamicResource Flyout}' BorderBrush='{DynamicResource FlyoutStroke}' BorderThickness='1' CornerRadius='8' Padding='0,4' Margin='8,4,8,12' MinWidth='220'>
          <Border.Effect><DropShadowEffect BlurRadius='18' ShadowDepth='5' Opacity='0.3'/></Border.Effect>
          <StackPanel IsItemsHost='True'/>
        </Border>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='MenuItem'>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='MenuItem'>
        <Border x:Name='bd' Background='Transparent' CornerRadius='4' Margin='4,1' Height='32'>
          <Grid Margin='12,0'>
            <Grid.ColumnDefinitions><ColumnDefinition Width='28'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
            <TextBlock x:Name='ico' Text='{TemplateBinding Tag}' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='14' VerticalAlignment='Center'/>
            <ContentPresenter Grid.Column='1' ContentSource='Header' VerticalAlignment='Center' RecognizesAccessKey='True'/>
            <TextBlock x:Name='chk' Grid.Column='2' Text='&#xE73E;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='12' VerticalAlignment='Center' Margin='16,0,0,0' Visibility='Collapsed'/>
          </Grid>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource SubtleHover}'/></Trigger>
          <Trigger Property='IsChecked' Value='True'><Setter TargetName='chk' Property='Visibility' Value='Visible'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{DynamicResource TextDisabled}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='Separator'>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Separator'><Border Height='1' Margin='0,4' Background='{DynamicResource Divider}'/></ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='ToolTip'>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ToolTip'>
        <Border Background='{DynamicResource Flyout}' BorderBrush='{DynamicResource FlyoutStroke}' BorderThickness='1' CornerRadius='4' Padding='8,5,8,7' Margin='4,4,4,8' MaxWidth='320'>
          <Border.Effect><DropShadowEffect BlurRadius='10' ShadowDepth='3' Opacity='0.25'/></Border.Effect>
          <ContentPresenter><ContentPresenter.Resources><Style TargetType='TextBlock'><Setter Property='TextWrapping' Value='Wrap'/></Style></ContentPresenter.Resources></ContentPresenter>
        </Border>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <!-- barra di scorrimento sottile -->
  <Style x:Key='ThumbStyle' TargetType='Thumb'>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Thumb'><Border CornerRadius='2' Background='{DynamicResource ControlStrong}'/></ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='6'/>
    <Setter Property='MinWidth' Value='6'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Track x:Name='PART_Track' IsDirectionReversed='True' Margin='1,4'>
          <Track.Thumb><Thumb Style='{StaticResource ThumbStyle}' Width='3'/></Track.Thumb>
        </Track>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
</ResourceDictionary>";
    }
}
