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

        OfficeWeb.NavigateToString("""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
html,body {
    margin:0;
    width:100%;
    height:100%;
    font-family:Segoe UI,Arial,sans-serif;
    background:#fafafa;
    color:#222;
}
.shell {
    height:100%;
    display:flex;
    align-items:center;
    justify-content:center;
}
.panel {
    width:560px;
    padding:36px;
    background:white;
    border:1px solid #ddd;
}
h1 {
    margin:0 0 10px 0;
    font-size:24px;
}
.status {
    font-family:Consolas,monospace;
    margin-top:24px;
    padding:14px;
    background:#f5f5f5;
}
</style>
</head>
<body>
<div class="shell">
    <div class="panel">
        <h1>KHZ Office Surface</h1>
        <p>Native WPF host + embedded WebView2 is running.</p>
        <p>The Office engine is intentionally not connected yet.</p>

        <div class="status">
            HOST: VERIFIED AT RUNTIME<br>
            WEBVIEW2: VERIFIED AT RUNTIME<br>
            OFFICE ENGINE: UNVERIFIED
        </div>
    </div>
</div>
</body>
</html>
""");
    }
}
