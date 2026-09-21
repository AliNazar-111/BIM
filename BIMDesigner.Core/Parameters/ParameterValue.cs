using System.Globalization;

namespace BIMDesigner.Core.Parameters;

/// <summary>
/// A parameter as seen by the property panel, and later by schedules and IFC export.
///
/// It does not store anything. It is a live window onto a real property of an element or
/// type, so <c>wall.UnconnectedHeight</c> and the "Unconnected Height" parameter can never
/// disagree. Computed values such as Length or Volume are exposed the same way, with no
/// setter, which is what makes them read-only in the UI.
/// </summary>
public sealed class ParameterValue
{
    private readonly Func<object?> _get;
    private readonly Func<object?, bool>? _set;

    private ParameterValue(ParameterDefinition definition, Func<object?> get, Func<object?, bool>? set)
    {
        Definition = definition;
        _get = get;
        _set = set;
    }

    public ParameterDefinition Definition { get; }

    public string Name => Definition.Name;

    public ParameterDataType DataType => Definition.DataType;

    public ParameterGroup Group => Definition.Group;

    public bool IsReadOnly => _set is null;

    /// <summary>
    /// The values this parameter will accept, when it is constrained to a fixed set
    /// (specification section 7, "data validation: allowed values"). Null when free.
    /// The property panel renders these as a dropdown.
    /// </summary>
    public IReadOnlyList<string>? AllowedValues { get; private init; }

    public object? Value => _get();

    /// <summary>The value as the user should see it, with units applied.</summary>
    public string DisplayValue => ParameterFormatter.Format(DataType, Value);

    /// <summary>Binds a read/write parameter to a property.</summary>
    public static ParameterValue Bind<T>(ParameterDefinition definition, Func<T> get, Action<T> set) =>
        new(definition, () => get(), raw =>
        {
            if (!TryConvert<T>(raw, out var typed)) return false;
            set(typed);
            return true;
        });

    /// <summary>
    /// Binds a parameter whose element validates the value, such as a wall height that must
    /// be positive. The setter returns false to refuse the write, which the property panel
    /// shows by reverting the field rather than pretending the edit took.
    /// </summary>
    public static ParameterValue BindValidated<T>(ParameterDefinition definition, Func<T> get, Func<T, bool> trySet) =>
        new(definition, () => get(), raw => TryConvert<T>(raw, out var typed) && trySet(typed));

    /// <summary>Binds a computed or otherwise read-only parameter.</summary>
    public static ParameterValue ReadOnly<T>(ParameterDefinition definition, Func<T> get) =>
        new(definition, () => get(), null);

    /// <summary>
    /// Binds a parameter restricted to a fixed set of values, such as a wall's location
    /// line or the levels available as its base constraint. Values outside the set are
    /// rejected rather than written.
    /// </summary>
    public static ParameterValue BindChoice(
        ParameterDefinition definition,
        Func<string> get,
        Action<string> set,
        IReadOnlyList<string> allowedValues) =>
        new(definition, () => get(), raw =>
        {
            var text = raw?.ToString();
            if (text is null || !allowedValues.Contains(text)) return false;

            set(text);
            return true;
        })
        {
            AllowedValues = allowedValues
        };

    /// <summary>
    /// Writes a value, converting it to the underlying property type. Returns false and
    /// changes nothing if the parameter is read-only or the value does not fit.
    /// </summary>
    public bool TrySet(object? value) => _set is not null && _set(value);

    /// <summary>Writes a value typed by the user, e.g. "3.20 m" into a length parameter.</summary>
    public bool TrySetFromText(string? text) =>
        ParameterFormatter.TryParse(DataType, text, out var parsed) && TrySet(parsed);

    private static bool TryConvert<T>(object? raw, out T value)
    {
        value = default!;

        if (raw is T already)
        {
            value = already;
            return true;
        }

        if (raw is null) return false;

        // Enum parameters arrive from the UI as their display text.
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target.IsEnum)
        {
            if (!Enum.TryParse(target, raw.ToString(), ignoreCase: true, out var parsed)) return false;
            value = (T)parsed;
            return true;
        }

        try
        {
            value = (T)Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    public override string ToString() => $"{Name} = {DisplayValue}";
}
