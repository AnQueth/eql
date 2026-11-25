using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EqLog.Overlay.Mvvm;

/// <summary>Base class for view models that raise <see cref="INotifyPropertyChanged.PropertyChanged"/>.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Assigns <paramref name="value"/> to <paramref name="field"/> and raises a change notification when it differs.</summary>
    /// <returns><see langword="true"/> when the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for <paramref name="propertyName"/>.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for each dependent property name.</summary>
    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }
}
