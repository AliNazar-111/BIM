using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Elements;

/// <summary>
/// A type is one configuration of a family (specification section 16, "Family / Object /
/// Type"). Its parameters are shared by every instance placed from it, so editing the
/// layers of "Exterior - Brick on Block" changes every such wall in the project at once.
///
/// This split is the core of BIM. Keeping it out of the model would leave us with CAD.
/// </summary>
public abstract class ElementType
{
    protected ElementType(string name)
    {
        Name = name;
    }

    public Guid Id { get; init; } = Guid.NewGuid();

    public abstract BuiltInCategory Category { get; }

    public string Name { get; set; }

    /// <summary>Short code shown by type tags on drawings.</summary>
    public string TypeMark { get; set; } = string.Empty;

    /// <summary>Classification code - Uniclass, OmniClass, UniFormat (section 7).</summary>
    public string AssemblyCode { get; set; } = string.Empty;

    public string Keynote { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Unit cost, the input to 5D quantity takeoff (section 9.3).</summary>
    public decimal Cost { get; set; }

    public abstract IEnumerable<ParameterValue> GetTypeParameters();

    /// <summary>Identity parameters every type carries.</summary>
    protected IEnumerable<ParameterValue> GetCommonTypeParameters()
    {
        yield return ParameterValue.Bind(CommonTypeParameters.TypeName, () => Name, v => Name = v);
        yield return ParameterValue.Bind(CommonTypeParameters.TypeMark, () => TypeMark, v => TypeMark = v);
        yield return ParameterValue.Bind(CommonTypeParameters.AssemblyCode, () => AssemblyCode, v => AssemblyCode = v);
        yield return ParameterValue.Bind(CommonTypeParameters.Keynote, () => Keynote, v => Keynote = v);
        yield return ParameterValue.Bind(CommonTypeParameters.Manufacturer, () => Manufacturer, v => Manufacturer = v);
        yield return ParameterValue.Bind(CommonTypeParameters.Cost, () => Cost, v => Cost = v);
        yield return ParameterValue.Bind(CommonTypeParameters.Url, () => Url, v => Url = v);
    }

    public override string ToString() => Name;
}

/// <summary>Type parameter definitions shared by every category (section 13).</summary>
public static class CommonTypeParameters
{
    public static readonly ParameterDefinition TypeName =
        new("Type Name", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition TypeMark =
        new("Type Mark", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition AssemblyCode =
        new("Assembly Code", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Keynote =
        new("Keynote", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Manufacturer =
        new("Manufacturer", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Cost =
        new("Cost", ParameterDataType.Currency, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Url =
        new("URL", ParameterDataType.Url, ParameterBinding.Type, ParameterGroup.IdentityData);
}
