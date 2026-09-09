using System.Globalization;
using System.IO;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KHZ.Reporting.Spike;

public partial class MainWindow
{
    private const long MaximumLogoBytes = 4L * 1024L * 1024L;

    private string _headerColor = "#4472C4";
    private string _alignment = "Center";
    private string? _logoPath;
    private double _previewZoom = 1.0;
    private bool _formattingInitialized;

    private void Formatting_Loaded(object sender, RoutedEventArgs e)
    {
        if (_formattingInitialized)
            return;

        _formattingInitialized = true;

        var fontNames = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        FontFamilyBox.ItemsSource = fontNames;
        SelectFont("Segoe UI");

        FontSizeBox.ItemsSource = new[]
        {
            "8", "9", "10", "11", "12", "14", "16", "17", "18", "20", "24", "28", "32", "36", "48"
        };
        FontSizeBox.Text = "17";
        BoldToggle.IsChecked = true;
        SetHeaderColor(_headerColor);
        UpdateZoomText();
    }

    private async void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var preset = SelectedComboText(TemplatePresetBox);

            switch (preset)
            {
                case "Slate":
                    SelectFont("Segoe UI");
                    FontSizeBox.Text = "17";
                    BoldToggle.IsChecked = true;
                    ItalicToggle.IsChecked = false;
                    _alignment = "Left";
                    SetHeaderColor("#3F4854");
                    break;

                case "Minimal":
                    SelectFont("Arial");
                    FontSizeBox.Text = "16";
                    BoldToggle.IsChecked = false;
                    ItalicToggle.IsChecked = false;
                    _alignment = "Center";
                    SetHeaderColor("#666666");
                    break;

                case "Arabic":
                    SelectFont("Tahoma");
                    FontSizeBox.Text = "18";
                    BoldToggle.IsChecked = true;
                    ItalicToggle.IsChecked = false;
                    _alignment = "Right";
                    SetHeaderColor("#2F5597");
                    break;

                default:
                    SelectFont("Segoe UI");
                    FontSizeBox.Text = "17";
                    BoldToggle.IsChecked = true;
                    ItalicToggle.IsChecked = false;
                    _alignment = "Center";
                    SetHeaderColor("#4472C4");
                    break;
            }

            ApplyFormattingToTemplate();
            await LoadReportAsync();
            StatusText.Text = $"Preset applied: {preset}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ApplyStyle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyFormattingToTemplate();
            await LoadReportAsync();
            StatusText.Text = $"Style applied to {SelectedComboText(FormatTargetBox)}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void AlignLeft_Click(object sender, RoutedEventArgs e) => _alignment = "Left";
    private void AlignCenter_Click(object sender, RoutedEventArgs e) => _alignment = "Center";
    private void AlignRight_Click(object sender, RoutedEventArgs e) => _alignment = "Right";

    private void PickHeaderColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        var color = dialog.Color;
        SetHeaderColor($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private async void SelectLogo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose report logo",
                Filter = "Logo image (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg",
                Multiselect = false,
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != true)
                return;

            var info = new FileInfo(dialog.FileName);
            if (info.Length > MaximumLogoBytes)
                throw new InvalidDataException($"Logo exceeds the {MaximumLogoBytes} byte limit.");

            _logoPath = dialog.FileName;
            LogoNameText.Text = info.Name;

            ApplyFormattingToTemplate();
            await LoadReportAsync();
            StatusText.Text = $"Logo applied: {info.Name}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ClearLogo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logoPath = null;
            LogoNameText.Text = "No logo";
            ApplyFormattingToTemplate();
            await LoadReportAsync();
            StatusText.Text = "Logo cleared";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _previewZoom = Math.Max(0.5, _previewZoom - 0.1);
        ApplyZoom();
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        _previewZoom = 1.0;
        ApplyZoom();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _previewZoom = Math.Min(2.0, _previewZoom + 0.1);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        PreviewScale.ScaleX = _previewZoom;
        PreviewScale.ScaleY = _previewZoom;
        UpdateZoomText();
    }

    private void UpdateZoomText()
        => ZoomText.Text = $"{_previewZoom * 100:0}%";

    private void ApplyFormattingToTemplate()
    {
        if (!File.Exists(_templatePath))
            throw new FileNotFoundException("Report template was not found.", _templatePath);

        var document = XDocument.Load(_templatePath, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidDataException("Report template has no root element.");

        var ns = root.Name.Namespace;
        var target = SelectedComboText(FormatTargetBox);
        var fontFamily = FontFamilyBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(fontFamily))
            fontFamily = FontFamilyBox.Text;
        if (string.IsNullOrWhiteSpace(fontFamily))
            fontFamily = "Segoe UI";

        if (!double.TryParse(FontSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize)
            || fontSize < 6
            || fontSize > 96)
        {
            throw new InvalidDataException("Font size must be between 6 and 96 points.");
        }

        var targetNames = target switch
        {
            "Company" => new[] { "Company" },
            "Table" => new[] { "HNo", "HTag", "HDescription", "HLocation", "RowNo", "Tag", "Description", "Location" },
            _ => new[] { "Title" }
        };

        foreach (var textBox in FindTextBoxes(root, ns, targetNames))
        {
            SetStyleValue(textBox, ns, "FontFamily", fontFamily);
            SetStyleValue(textBox, ns, "FontSize", $"{fontSize:0.##}pt");
            SetStyleValue(textBox, ns, "FontWeight", BoldToggle.IsChecked == true ? "Bold" : "Normal");
            SetStyleValue(textBox, ns, "FontStyle", ItalicToggle.IsChecked == true ? "Italic" : "Normal");
            SetStyleValue(textBox, ns, "TextAlign", _alignment);
        }

        foreach (var header in FindTextBoxes(root, ns, new[] { "HNo", "HTag", "HDescription", "HLocation" }))
        {
            SetStyleValue(header, ns, "BackgroundColor", _headerColor);
            SetStyleValue(header, ns, "Color", "White");
        }

        UpsertLogo(root, ns);
        document.Save(_templatePath, SaveOptions.DisableFormatting);
    }

    private void UpsertLogo(XElement root, XNamespace ns)
    {
        var embeddedImages = root.Element(ns + "EmbeddedImages");
        if (embeddedImages is not null)
        {
            embeddedImages.Elements(ns + "EmbeddedImage")
                .Where(item => string.Equals((string?)item.Attribute("Name"), "KHZLogoImage", StringComparison.Ordinal))
                .Remove();
        }

        var reportItems = root
            .Element(ns + "PageHeader")?
            .Element(ns + "ReportItems")
            ?? throw new InvalidDataException("Report header items are missing.");

        reportItems.Elements(ns + "Image")
            .Where(item => string.Equals((string?)item.Attribute("Name"), "KHZLogo", StringComparison.Ordinal))
            .Remove();

        var company = FindTextBoxes(root, ns, new[] { "Company" }).FirstOrDefault();

        if (string.IsNullOrWhiteSpace(_logoPath))
        {
            if (embeddedImages is not null && !embeddedImages.Elements(ns + "EmbeddedImage").Any())
                embeddedImages.Remove();

            if (company is not null)
            {
                SetChildValue(company, ns, "Left", "0in");
                SetChildValue(company, ns, "Width", "3.2in");
            }

            return;
        }

        var logoPath = Path.GetFullPath(_logoPath);
        var info = new FileInfo(logoPath);
        if (!info.Exists)
            throw new FileNotFoundException("Selected logo no longer exists.", logoPath);
        if (info.Length > MaximumLogoBytes)
            throw new InvalidDataException($"Logo exceeds the {MaximumLogoBytes} byte limit.");

        var extension = info.Extension.ToLowerInvariant();
        var mimeType = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => throw new InvalidDataException("Logo must be PNG or JPEG.")
        };

        embeddedImages ??= new XElement(ns + "EmbeddedImages");
        if (embeddedImages.Parent is null)
        {
            var dataSets = root.Element(ns + "DataSets");
            if (dataSets is not null)
                dataSets.AddAfterSelf(embeddedImages);
            else
                root.AddFirst(embeddedImages);
        }

        embeddedImages.Add(
            new XElement(ns + "EmbeddedImage",
                new XAttribute("Name", "KHZLogoImage"),
                new XElement(ns + "MIMEType", mimeType),
                new XElement(ns + "ImageData", Convert.ToBase64String(File.ReadAllBytes(logoPath)))));

        reportItems.AddFirst(
            new XElement(ns + "Image",
                new XAttribute("Name", "KHZLogo"),
                new XElement(ns + "Top", ".02in"),
                new XElement(ns + "Left", "0in"),
                new XElement(ns + "Width", "1.0in"),
                new XElement(ns + "Height", ".55in"),
                new XElement(ns + "Source", "Embedded"),
                new XElement(ns + "Value", "KHZLogoImage"),
                new XElement(ns + "Sizing", "FitProportional")));

        if (company is not null)
        {
            SetChildValue(company, ns, "Left", "1.12in");
            SetChildValue(company, ns, "Width", "2.08in");
        }
    }

    private static IEnumerable<XElement> FindTextBoxes(
        XElement root,
        XNamespace ns,
        IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        return root
            .Descendants(ns + "Textbox")
            .Where(item => wanted.Contains((string?)item.Attribute("Name") ?? ""));
    }

    private static void SetStyleValue(XElement textBox, XNamespace ns, string name, string value)
    {
        var style = textBox.Element(ns + "Style");
        if (style is null)
        {
            style = new XElement(ns + "Style");
            textBox.Add(style);
        }

        var element = style.Element(ns + name);
        if (element is null)
            style.Add(new XElement(ns + name, value));
        else
            element.Value = value;
    }

    private static void SetChildValue(XElement element, XNamespace ns, string name, string value)
    {
        var child = element.Element(ns + name);
        if (child is null)
            element.AddFirst(new XElement(ns + name, value));
        else
            child.Value = value;
    }

    private void SetHeaderColor(string value)
    {
        _headerColor = value;
        var color = (Color)ColorConverter.ConvertFromString(value);
        HeaderColorButton.Background = new SolidColorBrush(color);
    }

    private void SelectFont(string preferred)
    {
        if (FontFamilyBox.ItemsSource is not IEnumerable<string> fonts)
            return;

        var match = fonts.FirstOrDefault(font => string.Equals(font, preferred, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return;

        FontFamilyBox.SelectedItem = match;
    }

    private static string SelectedComboText(System.Windows.Controls.ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? "";

        return comboBox.SelectedItem?.ToString() ?? comboBox.Text ?? "";
    }
}
