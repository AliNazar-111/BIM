namespace BIMDesigner.Core.Views;

/// <summary>
/// How much of an element's construction a view draws (specification section 6.2).
///
/// A wall shows as a single filled body at coarse detail and as its individual layers at
/// medium and fine, which is why the same plan can serve a presentation drawing and a
/// construction detail.
/// </summary>
public enum DetailLevel
{
    Coarse,
    Medium,
    Fine
}
