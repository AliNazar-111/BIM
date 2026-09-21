using System.ComponentModel;
using System.Runtime.CompilerServices;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.ViewModels;

/// <summary>An accepted parameter edit, carrying what it takes to reverse it.</summary>
public sealed class ParameterCommittedEventArgs : EventArgs
{
    public ParameterCommittedEventArgs(ParameterValue parameter, object? oldValue, object? newValue)
    {
        Parameter = parameter;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public ParameterValue Parameter { get; }

    public object? OldValue { get; }

    public object? NewValue { get; }
}

/// <summary>
/// One row of the property panel.
///
/// The panel is generated from whatever parameters an element reports, not hand-written per
/// category. That is why adding doors, windows and rooms later needs no new property-panel
/// XAML: they simply return their own parameters.
/// </summary>
public sealed class ParameterRow : INotifyPropertyChanged
{
    private readonly ParameterValue _parameter;

    public ParameterRow(ParameterValue parameter)
    {
        _parameter = parameter;
    }

    public ParameterValue Parameter => _parameter;

    public string Name => _parameter.Name;

    public string GroupName => EnumText.Humanise(_parameter.Group.ToString());

    public bool IsReadOnly => _parameter.IsReadOnly;

    /// <summary>Rendered as a dropdown of allowed values.</summary>
    public bool IsChoice => !IsReadOnly && _parameter.AllowedValues is { Count: > 0 };

    /// <summary>Rendered as a checkbox.</summary>
    public bool IsFlag => !IsReadOnly && !IsChoice && _parameter.DataType == ParameterDataType.YesNo;

    /// <summary>Rendered as a text box.</summary>
    public bool IsEditableText => !IsReadOnly && !IsChoice && !IsFlag;

    public IReadOnlyList<string> Choices => _parameter.AllowedValues ?? Array.Empty<string>();

    /// <summary>
    /// The value as text. A write the parameter rejects - an unparseable length, a negative
    /// height - leaves the model untouched, and the row reverts to the stored value.
    /// </summary>
    public string Text
    {
        get => _parameter.DisplayValue;
        set
        {
            if (value == _parameter.DisplayValue) return;
            Commit(() => _parameter.TrySetFromText(value));
        }
    }

    public string SelectedChoice
    {
        get => _parameter.DisplayValue;
        set
        {
            if (value == _parameter.DisplayValue) return;
            Commit(() => _parameter.TrySet(value));
        }
    }

    public bool Flag
    {
        get => _parameter.Value is true;
        set
        {
            if (value == Flag) return;
            Commit(() => _parameter.TrySet(value));
        }
    }

    /// <summary>
    /// Applies a write, capturing the value on either side of it so the edit can be undone.
    /// </summary>
    private void Commit(Func<bool> write)
    {
        var oldValue = _parameter.Value;
        var accepted = write();
        var newValue = _parameter.Value;

        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(Flag));

        if (accepted && !Equals(oldValue, newValue))
            ValueCommitted?.Invoke(this, new ParameterCommittedEventArgs(_parameter, oldValue, newValue));
    }

    /// <summary>Raised after a write the model accepted, so it can be recorded and redrawn.</summary>
    public event EventHandler<ParameterCommittedEventArgs>? ValueCommitted;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
