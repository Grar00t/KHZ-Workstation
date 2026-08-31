namespace KHZ.App.Trust;

internal interface IActivityStore
{
    void Record(
        string category,
        string action,
        string? target,
        string result,
        object? details = null,
        string actor = "local-user");
}
