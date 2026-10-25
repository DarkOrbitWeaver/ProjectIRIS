using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CompanionOS.Core.Common;

/// <summary>
/// The smallest possible change notifier.
///
/// Modules and screens both need to announce "this value changed" so the interface can
/// follow along. Rather than pull in a whole MVVM framework for that one ability, the
/// framework implements it here in fifteen readable lines.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises the change notification for one property.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Assigns a field and notifies only when the value actually changed.</summary>
    protected bool SetProperty<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, newValue))
        {
            return false;
        }

        field = newValue;
        OnPropertyChanged(propertyName);
        return true;
    }
}
