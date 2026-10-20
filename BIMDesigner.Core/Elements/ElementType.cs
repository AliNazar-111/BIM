using BIMDesigner.Core.Documents;
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

    /// <summary>Its code in the project's classification - Uniclass 2015 to start with: what IFC and COBie carry.</summary>
    public string ClassificationCode { get; set; } = string.Empty;

    /// <summary>The values of the project's parameters on its types, by the parameter's id.</summary>
    public Dictionary<Guid, object?> ParameterValues { get; } = new();

    /// <summary>
    /// Every parameter it has: its own, its classification, and the project's parameters on its
    /// category's types - what Type Properties lists and a schedule reads.
    /// </summary>
    public IEnumerable<ParameterValue> AllTypeParameters(BimDocument document)
    {
        foreach (var parameter in GetTypeParameters(document)) yield return parameter;

        var codes = document.Classification.Entries.Select(entry => entry.Code).ToList();
        yield return codes.Count == 0
            ? ParameterValue.Bind(CommonTypeParameters.Classification, () => ClassificationCode, v => ClassificationCode = v?.Trim() ?? string.Empty)
            : ParameterValue.BindChoice(CommonTypeParameters.Classification, () => ClassificationCode, v => ClassificationCode = v?.Trim() ?? string.Empty,
                new[] { string.Empty }.Concat(codes).Append(ClassificationCode).Distinct().ToList());
        yield return ParameterValue.ReadOnly(CommonTypeParameters.ClassificationTitle, () => document.Classification.Find(ClassificationCode)?.Title ?? string.Empty);

        foreach (var parameter in ProjectParameters.For(document, Category, ParameterBinding.Type, ParameterValues)) yield return parameter;
    }

    /// <summary>
    /// Gives a copy of this type the identity every type carries - its mark, classification,
    /// keynote, maker, link, description and cost - so a duplicate starts as this one is.
    /// </summary>
    protected T WithCommonOf<T>(T copy) where T : ElementType
    {
        copy.TakeIdentityFrom(this);
        return copy;
    }

    /// <summary>Takes another type's mark, classification, keynote, maker, link, description and cost.</summary>
    public void TakeIdentityFrom(ElementType other)
    {
        TypeMark = other.TypeMark;
        AssemblyCode = other.AssemblyCode;
        Keynote = other.Keynote;
        Manufacturer = other.Manufacturer;
        Url = other.Url;
        Description = other.Description;
        Cost = other.Cost;
        ClassificationCode = other.ClassificationCode;
        ParameterValues.Clear();
        foreach (var (id, value) in other.ParameterValues) ParameterValues[id] = value;
    }

    /// <summary>
    /// The parameters shared by every element of this type. The document is passed so a type
    /// can offer the project.s materials by name; a type that needs none ignores it.
    /// </summary>
    public abstract IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null);

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

    public static readonly ParameterDefinition Classification =
        new("Classification", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition ClassificationTitle =
        new("Classification Title", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

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
