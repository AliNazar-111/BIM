using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Views;

/// <summary>
/// Elements that could not be drawn.
///
/// One broken element - a wall left in a shape nothing expected, a family that cannot be built -
/// would otherwise take the whole plan or 3D view down with it, and with it everything else in
/// the building. Instead each element is drawn in turn, and one that fails is left out of that
/// drawing and said so: the rest of the building is still there to work on, and to save.
/// </summary>
public static class DrawFailures
{
    /// <summary>Raised for each element that could not be drawn, with what went wrong.</summary>
    public static event Action<Element, Exception>? Failed;

    /// <summary>Draws one element; if that goes wrong, says so and carries on. False when it did not draw.</summary>
    public static bool Guard(Element element, Action draw)
    {
        try
        {
            draw();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Failed?.Invoke(element, exception);
            return false;
        }
    }
}
