using System.Windows;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Parameters;
using Window = System.Windows.Window;

namespace BIMDesigner.UI;

/// <summary>
/// Edits one curtain wall's grid and panels (specification section 3.1, "curtain walls"). The
/// wall keeps following its type's grid unless a line is added, moved or taken away here;
/// changing panels alone leaves the grid to the type.
/// </summary>
public partial class EditCurtainGridWindow : Window
{
    private readonly CurtainWallType _type;
    private readonly IReadOnlyList<BIMDesigner.Core.Geometry.Point2D>? _topLine;
    private readonly double _length;
    private readonly double _height;
    private readonly bool _hadOwnGrid;
    private bool _followType;
    private readonly List<(double From, double To, double Sill, double Head)> _windows;

    public EditCurtainGridWindow(BimDocument document, Wall wall)
    {
        InitializeComponent();
        ScreenFit.Apply(this);

        var layout = CurtainLayout.Of(document, wall)
                     ?? throw new InvalidOperationException("Only a curtain wall has a curtain grid.");

        _type = layout.Type;
        _length = layout.Length;
        _height = layout.Height;
        _topLine = layout.TopLine;
        _hadOwnGrid = wall.CurtainGrid is not null;
        // The windows cut into this wall, so the elevation shows them where they are.
        _windows = WallOpenings.Of(document, wall)
            .Select(opening => (Opening: opening, Type: document.FindType<OpeningType>(opening.TypeId)))
            .Where(entry => entry.Type is not null)
            .Select(entry =>
            {
                var (from, to) = entry.Opening.GetSpan(entry.Type!);
                return (from, to, entry.Opening.SillHeight, entry.Opening.SillHeight + entry.Opening.HeightOf(entry.Type!));
            })
            .ToList();

        WallCaption.Text = $"ELEVATION  -  {_type.Name.ToUpperInvariant()}, {Units.FormatLength(_length)} BY {Units.FormatLength(_height)}, ITS START ON THE LEFT";

        Editor.Show(_type, _length, _height, Inner(layout.Verticals), Inner(layout.Horizontals),
            wall.CurtainPanels ?? Array.Empty<CurtainPanelOverride>(), _windows, _topLine, wall.CurtainGrid?.Removed);
        // Which door a door panel becomes: any door type in the project, as Revit picks a
        // curtain wall door in the Type Selector.
        var doorTypes = CurtainDoors.TypesFor(document);
        DoorTypeBox.ItemsSource = doorTypes;
        DoorTypeBox.SelectedItem =
            doorTypes.FirstOrDefault(t => t.Id == (wall.CurtainPanels ?? Array.Empty<CurtainPanelOverride>())
                .FirstOrDefault(p => p.Kind == CurtainPanelKind.Door && p.OpeningTypeId is not null).OpeningTypeId)
            ?? doorTypes.FirstOrDefault();

        // Which window a click cuts into the wall.
        var windowTypes = document.TypesOf<WindowType>().OrderBy(type => type.Name).ToList();
        WindowTypeBox.ItemsSource = windowTypes;
        WindowTypeBox.SelectedItem = windowTypes.FirstOrDefault();

        GlassKindBox.ItemsSource = EnumText.Choices<CurtainGlass>();
        GlassKindBox.SelectedIndex = 0;

        Editor.Refused += (_, message) => MessageText.Text = message;
        Editor.Edited += (_, _) => MessageText.Text = string.Empty;
    }

    /// <summary>The wall's own grid to set, or null to follow its type's.</summary>
    public CurtainGrid? ResultGrid { get; private set; }

    /// <summary>The panels that are not glass.</summary>
    public IReadOnlyList<CurtainPanelOverride>? ResultPanels { get; private set; }

    /// <summary>The windows to cut into the wall, added here.</summary>
    public IReadOnlyList<(Guid TypeId, double DistanceAlongWall, double SillHeight)> ResultWindows { get; private set; } =
        Array.Empty<(Guid, double, double)>();

    private static IEnumerable<double> Inner(IReadOnlyList<double> lines) => lines.Skip(1).SkipLast(1);

    private void OnFillChanged(object sender, RoutedEventArgs e)
    {
        if (Editor is null) return;

        // A window is not a panel choice: it is cut into the wall where the panel is.
        Editor.AddWindow = sender == WindowChoice ? WindowTypeBox?.SelectedItem as WindowType : null;

        Editor.FillWith = sender == SolidChoice ? CurtainPanelKind.Solid
            : sender == EmptyChoice ? CurtainPanelKind.Empty
            : sender == DoorChoice ? CurtainPanelKind.Door
            : CurtainPanelKind.Glazed;

        // The panel takes whichever type goes with the choice just made.
        Editor.OpeningTypeId = Editor.FillWith switch
        {
            CurtainPanelKind.Door => (DoorTypeBox.SelectedItem as DoorType)?.Id,
            _ => null
        };
    }

    private void OnGlassKindChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Editor is not null && EnumText.TryParse<CurtainGlass>(GlassKindBox.SelectedItem as string, out var glass))
            Editor.FillGlass = glass;
    }

    private void OnDoorTypeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Editor is not null && DoorChoice?.IsChecked == true)
            Editor.OpeningTypeId = (DoorTypeBox.SelectedItem as DoorType)?.Id;
    }

    private void OnWindowTypeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Editor is not null && WindowChoice?.IsChecked == true)
            Editor.AddWindow = WindowTypeBox.SelectedItem as WindowType;
    }

    /// <summary>Where in the panel clicked a window goes: along it and up it, from its middle.</summary>
    private void OnWindowOffsetChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // The boxes raise this as they are built, before the rest of the window exists.
        if (Editor is null || WindowAlongBox is null || WindowUpBox is null) return;

        Editor.WindowAlong = Length(WindowAlongBox.Text);
        Editor.WindowUp = Length(WindowUpBox.Text);

        static double Length(string text) =>
            ParameterFormatter.TryParse(ParameterDataType.Length, text, out var parsed) && parsed is double value ? value : 0;
    }

    private void OnAddVertical(object sender, RoutedEventArgs e) => AddLine(true, VerticalAtBox.Text);

    private void OnAddHorizontal(object sender, RoutedEventArgs e) => AddLine(false, HorizontalAtBox.Text);

    private void AddLine(bool vertical, string text)
    {
        if (!ParameterFormatter.TryParse(ParameterDataType.Length, text, out var parsed) || parsed is not double at)
        {
            MessageText.Text = vertical
                ? "Type how far along the wall, such as 2400 or 2.4 m."
                : "Type how high above the base, such as 2100 or 2.1 m.";
            return;
        }

        Editor.AddLine(vertical, at);
    }

    private void OnDeleteLine(object sender, RoutedEventArgs e)
    {
        if (Editor.Selected is null)
        {
            MessageText.Text = "Click a grid line on the drawing to select a stretch of it, or double-click it for all of it, first.";
            return;
        }

        Editor.RemoveSelected();
    }

    private void OnFollowType(object sender, RoutedEventArgs e)
    {
        Editor.Show(_type, _length, _height,
            Inner(CurtainLayout.Build(_type, _length, _height, null, null).Verticals),
            Inner(CurtainLayout.Build(_type, _length, _height, null, null).Horizontals),
            Array.Empty<CurtainPanelOverride>(), _windows, _topLine);
        _followType = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // A grid only becomes the wall's own once a line has been changed; otherwise it keeps
        // following the type, and stretching the wall re-spaces it.
        var ownGrid = Editor.GridEdited || (_hadOwnGrid && !_followType);
        ResultGrid = ownGrid ? new CurtainGrid(Editor.Verticals.ToList(), Editor.Horizontals.ToList()).WithRemoved(Editor.Removed) : null;
        ResultPanels = Editor.Panels.Count == 0 ? null : Editor.Panels;
        ResultWindows = Editor.AddedWindows;
        DialogResult = true;
    }
}
