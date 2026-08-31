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
            "http://localhost:8090/editor"
        );
    }
}
