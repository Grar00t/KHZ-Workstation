using System.Windows;

namespace KHZ.AssetRegister;

public partial class AuditWindow : Window
{
    private readonly AssetStore _store;

    public AuditWindow(AssetStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        LoadAudit();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => LoadAudit();

    private void LoadAudit()
    {
        var rows = _store.LoadAudit();
        AuditGrid.ItemsSource = rows;
        HeaderText.Text = _store.DatabasePath;
        StatusText.Text = $"Audit rows shown: {rows.Count:N0}";
    }
}
