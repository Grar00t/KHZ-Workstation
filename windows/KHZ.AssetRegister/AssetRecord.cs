using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KHZ.AssetRegister;

public sealed class AssetRecord : INotifyPropertyChanged
{
    private long _assetId;
    private string _assetTag = string.Empty;
    private string _serialNumber = string.Empty;
    private string _barcode = string.Empty;
    private string _category = string.Empty;
    private string _description = string.Empty;
    private string _manufacturer = string.Empty;
    private string _model = string.Empty;
    private string _location = string.Empty;
    private string _department = string.Empty;
    private string _custodian = string.Empty;
    private string _status = "In Service";
    private string _purchaseDate = string.Empty;
    private decimal _purchaseCost;
    private string _warrantyExpiry = string.Empty;
    private string _condition = string.Empty;
    private string _notes = string.Empty;
    private string _createdAt = string.Empty;
    private bool _isDirty = true;

    public long AssetId
    {
        get => _assetId;
        set => Set(ref _assetId, value, trackDirty: false);
    }

    public string AssetTag
    {
        get => _assetTag;
        set => Set(ref _assetTag, value);
    }

    public string SerialNumber
    {
        get => _serialNumber;
        set => Set(ref _serialNumber, value);
    }

    public string Barcode
    {
        get => _barcode;
        set => Set(ref _barcode, value);
    }

    public string Category
    {
        get => _category;
        set => Set(ref _category, value);
    }

    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    public string Manufacturer
    {
        get => _manufacturer;
        set => Set(ref _manufacturer, value);
    }

    public string Model
    {
        get => _model;
        set => Set(ref _model, value);
    }

    public string Location
    {
        get => _location;
        set => Set(ref _location, value);
    }

    public string Department
    {
        get => _department;
        set => Set(ref _department, value);
    }

    public string Custodian
    {
        get => _custodian;
        set => Set(ref _custodian, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string PurchaseDate
    {
        get => _purchaseDate;
        set => Set(ref _purchaseDate, value);
    }

    public decimal PurchaseCost
    {
        get => _purchaseCost;
        set => Set(ref _purchaseCost, value);
    }

    public string WarrantyExpiry
    {
        get => _warrantyExpiry;
        set => Set(ref _warrantyExpiry, value);
    }

    public string Condition
    {
        get => _condition;
        set => Set(ref _condition, value);
    }

    public string Notes
    {
        get => _notes;
        set => Set(ref _notes, value);
    }

    public string CreatedAt
    {
        get => _createdAt;
        set => Set(ref _createdAt, value, trackDirty: false);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value, trackDirty: false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void MarkClean() => IsDirty = false;

    public void MarkDirty() => IsDirty = true;

    private void Set<T>(
        ref T field,
        T value,
        bool trackDirty = true,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;

        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));

        if (trackDirty && propertyName != nameof(IsDirty))
            IsDirty = true;
    }
}
