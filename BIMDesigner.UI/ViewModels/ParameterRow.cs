using System.ComponentModel;
using System.Runtime.CompilerServices;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.ViewModels;

/// <summary>An accepted parameter edit, carrying what it takes to reverse it: one change per element edited.</summary>
public sealed class ParameterCommittedEventArgs : EventArgs
{
    public ParameterCommittedEventArgs(IReadOnlyList<(ParameterValue Parameter, object? OldValue, object? NewValue)> changes)
    {
        Changes = changes;
    }

    public IReadOnlyList<(ParameterValue Parameter, object? OldValue, object? NewValue)> Changes { get; }
}

/// <summary>
/// One row of the property panel.
///
/// The panel is generated from whatever parameters an element reports, not hand-written per
/// category. That is why adding doors, windows and rooms later needs no new property-panel
/// XAML: they simply return their own parameters.
///
/// With several elements selected a row stands for the same parameter on each of them, as in
/// Revit: it shows the value when they agree and is left blank when they differ, and an edit
/// goes to all of them at once.
/// </summary>
public sealed class ParameterRow : INotifyPropertyChanged
{
    private readonly IReadOnlyList<ParameterValue> _parameters;
    private readonly ParameterValue _parameter;

    public ParameterRow(ParameterValue parameter) : this(new[] { parameter })
    {
    }

    public ParameterRow(IReadOnlyList<ParameterValue> parameters)
    {
        if (parameters.Count == 0) throw new ArgumentException("A row needs a parameter.", nameof(parameters));
        _parameters = parameters;
        _parameter = parameters[0];
    }

    public ParameterValue Parameter => _parameter;

    public string Name => _parameter.Name;

    public string GroupName => EnumText.Humanise(_parameter.Group.ToString());

    public bool IsReadOnly => _parameter.IsReadOnly;

    /// <summary>Whether the elements the row stands for have different values.</summary>
    public bool Varies => _parameters.Skip(1).Any(p => p.DisplayValue != _parameter.DisplayValue);

    /// <summary>Rendered as a dropdown of allowed values.</summary>
    public bool IsChoice => !IsReadOnly && _parameter.AllowedValues is { Count: > 0 };

    /// <summary>Rendered as a checkbox.</summary>
    public bool IsFlag => !IsReadOnly && !IsChoice && _parameter.DataType == ParameterDataType.YesNo;

    /// <summary>Rendered as a text box.</summary>
    public bool IsEditableText => !IsReadOnly && !IsChoice && !IsFlag;

    public IReadOnlyList<string> Choices => _parameter.AllowedValues ?? Array.Empty<string>();

    /// <summary>
    /// The value as text, blank when the elements differ. A write the parameter rejects - an
    /// unparseable length, a negative height - leaves the model untouched, and the row reverts
    /// to the stored value.
    /// </summary>
    public string Text
    {
        get => Varies ? string.Empty : _parameter.DisplayValue;
        set
        {
            if (value == Text) return;
            Commit(parameter => parameter.TrySetFromText(value));
        }
    }

    public string? SelectedChoice
    {
        get => Varies ? null : _parameter.DisplayValue;
        set
        {
            if (value is null || value == SelectedChoice) return;
            Commit(parameter => parameter.TrySet(value));
        }
    }

    /// <summary>Ticked, clear, or neither when the elements differ.</summary>
    public bool? Flag
    {
        get => Varies ? null : _parameter.Value is true;
        set
        {
            if (value is not { } flag || value == Flag) return;
            Commit(parameter => parameter.TrySet(flag));
        }
    }

    /// <summary>
    /// Applies a write to every element the row stands for, capturing the value on either
    /// side of each so the edit can be undone as one step.
    /// </summary>
    private void Commit(Func<ParameterValue, bool> write)
    {
        var changes = new List<(ParameterValue, object?, object?)>();
        foreach (var parameter in _parameters)
        {
            var oldValue = parameter.Value;
            if (write(parameter) && !Equals(oldValue, parameter.Value))
                changes.Add((parameter, oldValue, parameter.Value));
        }

        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(Flag));

        if (changes.Count > 0) ValueCommitted?.Invoke(this, new ParameterCommittedEventArgs(changes));
    }

    /// <summary>Raised after a write the model accepted, so it can be recorded and redrawn.</summary>
    public event EventHandler<ParameterCommittedEventArgs>? ValueCommitted;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
