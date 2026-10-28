namespace Hindsight.Core.Settings;

/// <summary>Holds the live settings and persists them. <see cref="Changed"/> fires after every save.</summary>
public sealed class SettingsStore
{
    public SettingsStore()
    {
        Current = (JsonFile.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings()).Normalize();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Update(Action<AppSettings> change)
    {
        var copy = Current.Clone();
        change(copy);
        Current = copy.Normalize();
        JsonFile.Save(AppPaths.SettingsFile, Current);
        Changed?.Invoke(this, Current);
    }
}
