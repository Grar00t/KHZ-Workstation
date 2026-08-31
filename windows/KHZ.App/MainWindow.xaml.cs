using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Windows;

namespace KHZ.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var dataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KHZ",
            "WebView2"
        );

        Directory.CreateDirectory(dataPath);

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: dataPath
        );

        await OfficeWeb.EnsureCoreWebView2Async(env);

        OfficeWeb.CoreWebView2.Navigate(
            "http://localhost:8090/editor/sheet"
        );
    }

    private void NavigateOffice(string kind)
    {
        if (OfficeWeb.CoreWebView2 is null)
            return;

        SectionTitle.Text = kind switch
        {
            "document" => "Documents",
            "sheet" => "Sheets",
            "slide" => "Slides",
            "pdf" => "PDF",
            _ => "Office"
        };

        OfficeWeb.CoreWebView2.Navigate(
            $"http://localhost:8090/editor/{kind}"
        );
    }

    private void Documents_Click(object sender, RoutedEventArgs e)
        => NavigateOffice("document");

    private void Sheets_Click(object sender, RoutedEventArgs e)
        => NavigateOffice("sheet");

    private void Slides_Click(object sender, RoutedEventArgs e)
        => NavigateOffice("slide");

    private void Pdf_Click(object sender, RoutedEventArgs e)
        => NavigateOffice("pdf");

}
