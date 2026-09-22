namespace BIMDesigner.Core.Parameters;

/// <summary>
/// Parameter data types from specification section 2.1. The type decides how a value is
/// stored, formatted for display and parsed back from user input.
/// </summary>
public enum ParameterDataType
{
    /// <summary>Stored in millimetres.</summary>
    Length,

    /// <summary>Stored in square millimetres.</summary>
    Area,

    /// <summary>Stored in cubic millimetres.</summary>
    Volume,

    /// <summary>Stored in degrees.</summary>
    Angle,

    Number,
    Integer,
    Text,
    YesNo,
    Material,
    Currency,
    Url,
    Image,
    MultilineText
}

/// <summary>
/// Whether a parameter belongs to one placed element or is shared by every element of a
/// type (specification section 2.1, "Type vs. Instance").
/// </summary>
public enum ParameterBinding
{
    Instance,
    Type
}

/// <summary>
/// Grouping used to organise the property panel and, later, schedule field lists.
/// </summary>
public enum ParameterGroup
{
    Constraints,
    Dimensions,
    Construction,
    MaterialsAndFinishes,
    IdentityData,
    Analytical,
    Phasing,
    Other,

    /// <summary>How a view is drawn: its scale, detail level, style.</summary>
    Graphics
}
