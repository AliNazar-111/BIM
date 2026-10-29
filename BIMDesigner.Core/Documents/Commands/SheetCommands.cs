using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Documents.Commands;

/// <summary>Places a view on a sheet (specification section 6.5).</summary>
public sealed class AddViewportCommand : IUndoableCommand
{
    private readonly Sheet _sheet;
    private readonly Viewport _viewport;

    public AddViewportCommand(Sheet sheet, Viewport viewport, string name)
    {
        _sheet = sheet;
        _viewport = viewport;
        Name = name;
    }

    public string Name { get; }

    public Viewport Viewport => _viewport;

    public void Redo() => _sheet.Add(_viewport);

    public void Undo() => _sheet.Remove(_viewport);
}

/// <summary>
/// Takes a view off a sheet.
///
/// It remembers where in the list the viewport sat, so undo puts it back in the same drawing
/// order rather than on top of everything else.
/// </summary>
public sealed class RemoveViewportCommand : IUndoableCommand
{
    private readonly Sheet _sheet;
    private readonly Viewport _viewport;
    private readonly int _index;

    public RemoveViewportCommand(Sheet sheet, Viewport viewport)
    {
        _sheet = sheet;
        _viewport = viewport;
        _index = sheet.IndexOf(viewport);
    }

    public string Name => "Remove View from Sheet";

    public void Redo() => _sheet.Remove(_viewport);

    public void Undo()
    {
        if (_index >= 0 && _index <= _sheet.Viewports.Count) _sheet.Insert(_index, _viewport);
        else _sheet.Add(_viewport);
    }
}

/// <summary>Moves a viewport to another spot on the paper.</summary>
public sealed class MoveViewportCommand : IUndoableCommand
{
    private readonly Viewport _viewport;
    private readonly Point2D _from;
    private readonly Point2D _to;

    public MoveViewportCommand(Viewport viewport, Point2D from, Point2D to)
    {
        _viewport = viewport;
        _from = from;
        _to = to;
    }

    public string Name => "Move View on Sheet";

    public void Redo() => _viewport.Centre = _to;

    public void Undo() => _viewport.Centre = _from;
}

/// <summary>
/// Changes the scale a viewport is drawn at.
///
/// This is a change to the drawing, not to the building, which is why it is its own command
/// rather than a parameter edit on anything in the model.
/// </summary>
public sealed class SetViewportScaleCommand : IUndoableCommand
{
    private readonly Viewport _viewport;
    private readonly ViewScale _from;
    private readonly ViewScale _to;
    private readonly Point2D _fromCentre;
    private readonly Point2D _toCentre;

    /// <summary>
    /// Changes the scale, and moves the view to <paramref name="centre"/> at the same time when
    /// the new size no longer fits where it was. Both happen in one step, so one undo puts the
    /// view back exactly as it was rather than at the old scale in the new place.
    /// </summary>
    public SetViewportScaleCommand(Viewport viewport, ViewScale to, Point2D? centre = null)
    {
        _viewport = viewport;
        _from = viewport.Scale;
        _to = to;
        _fromCentre = viewport.Centre;
        _toCentre = centre ?? viewport.Centre;
    }

    public string Name => $"Set Scale {_to}";

    public void Redo()
    {
        _viewport.Scale = _to;
        _viewport.Centre = _toCentre;
    }

    public void Undo()
    {
        _viewport.Scale = _from;
        _viewport.Centre = _fromCentre;
    }

    /// <summary>
    /// Builds the command for a sheet, working out where the view has to go at its new size.
    /// </summary>
    public static SetViewportScaleCommand On(BimDocument document, Sheet sheet, Viewport viewport, ViewScale to)
    {
        var original = viewport.Scale;

        // Measure at the new scale, then put it back: the command is what changes the model.
        viewport.Scale = to;
        var centre = sheet.PositionAfterResize(document, viewport);
        viewport.Scale = original;

        return new SetViewportScaleCommand(viewport, to, centre);
    }
}

/// <summary>A picture put on a sheet.</summary>
public sealed class AddSheetImageCommand : IUndoableCommand
{
    private readonly Sheet _sheet;
    private readonly SheetImage _image;

    public AddSheetImageCommand(Sheet sheet, SheetImage image, string name = "Place Image on Sheet")
    {
        _sheet = sheet;
        _image = image;
        Name = name;
    }

    public string Name { get; }

    public SheetImage Image => _image;

    public void Redo() => _sheet.AddImage(_image);

    public void Undo() => _sheet.RemoveImage(_image);
}

/// <summary>A picture taken off a sheet, put back where it was in the order on undo.</summary>
public sealed class RemoveSheetImageCommand : IUndoableCommand
{
    private readonly Sheet _sheet;
    private readonly SheetImage _image;
    private int _index = -1;

    public RemoveSheetImageCommand(Sheet sheet, SheetImage image)
    {
        _sheet = sheet;
        _image = image;
    }

    public string Name => "Remove Image from Sheet";

    public void Redo()
    {
        _index = _sheet.IndexOfImage(_image);
        _sheet.RemoveImage(_image);
    }

    public void Undo() => _sheet.InsertImage(_index < 0 ? _sheet.Images.Count : _index, _image);
}

/// <summary>A picture's place, size and title changed, as one step: dragged, made wider, retitled.</summary>
public sealed class SetSheetImageCommand : IUndoableCommand
{
    private readonly SheetImage _image;
    private readonly (Point2D Centre, double Width, bool ShowTitle, string Title) _before;
    private readonly (Point2D Centre, double Width, bool ShowTitle, string Title) _after;

    public SetSheetImageCommand(SheetImage image, (Point2D Centre, double Width, bool ShowTitle, string Title) before, string name = "Change Image on Sheet")
    {
        _image = image;
        _before = before;
        _after = (image.Centre, image.Width, image.ShowTitle, image.Title);
        Name = name;
    }

    public string Name { get; }

    public static (Point2D Centre, double Width, bool ShowTitle, string Title) StateOf(SheetImage image) => (image.Centre, image.Width, image.ShowTitle, image.Title);

    public void Redo() => (_image.Centre, _image.Width, _image.ShowTitle, _image.Title) = _after;

    public void Undo() => (_image.Centre, _image.Width, _image.ShowTitle, _image.Title) = _before;
}
