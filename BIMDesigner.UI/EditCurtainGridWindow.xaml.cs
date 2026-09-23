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
    private readonly double _length;
    private readonly double _height;
    private readonly bool _hadOwnGrid;
    private bool _followType;

    public EditCurtainGridWindow(BimDocument document, Wall wall)
    {
        InitializeComponent();
        ScreenFit.Apply(this);

        var layout = CurtainLayout.Of(document, wall)
                     ?? throw new InvalidOperationException("Only a curtain wall has a curtain grid.");

        _type = layout.Type;
        _length = layout.Length;
        _height = layout.Height;
        _hadOwnGrid = wall.CurtainGrid is not null;

        WallCaption.Text = $"ELEVATION  -  {_type.Name.ToUpperInvariant()}, {Units.FormatLength(_length)} BY {Units.FormatLength(_height)}, ITS START ON THE LEFT";

        Editor.Show(_type, _length, _height, Inner(layout.Verticals), Inner(layout.Horizontals),
            wall.CurtainPanels ?? Array.Empty<CurtainPanelOverride>());
        // Which door a door panel becomes: any door type in the project, as Revit picks a
        // curtain wall door in the Type Selector.
        var doorTypes = document.TypesOf<DoorType>().OrderBy(t => t.Name).ToList();
        DoorTypeBox.ItemsSource = doorTypes;
        DoorTypeBox.SelectedItem =
            doorTypes.FirstOrDefault(t => t.Id == (wall.CurtainPanels ?? Array.Empty<CurtainPanelOverride>())
                .FirstOrDefault(p => p.Kind == CurtainPanelKind.Door && p.DoorTypeId is not null).DoorTypeId)
            ?? doorTypes.FirstOrDefault();

        Editor.Refused += (_, message) => MessageText.Text = message;
        Editor.Edited += (_, _) => MessageText.Text = string.Empty;
    }

    /// <summary>The wall's own grid to set, or null to follow its type's.</summary>
    public CurtainGrid? ResultGrid { get; private set; }

    /// <summary>The panels that are not glass.</summary>
    public IReadOnlyList<CurtainPanelOverride>? ResultPanels { get; private set; }

    private static IEnumerable<double> Inner(IReadOnlyList<double> lines) => lines.Skip(1).SkipLast(1);

    private void OnFillChanged(object sender, RoutedEventArgs e)
    {
        if (Editor is null) return;

        Editor.FillWith = sender == SolidChoice ? CurtainPanelKind.Solid
            : sender == EmptyChoice ? CurtainPanelKind.Empty
            : sender == DoorChoice ? CurtainPanelKind.Door
            : CurtainPanelKind.Glazed;
    }

    private void OnDoorTypeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Editor is not null) Editor.DoorTypeId = (DoorTypeBox.SelectedItem as DoorType)?.Id;
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
            MessageText.Text = "Click a grid line on the drawing to select it first.";
            return;
        }

        Editor.RemoveSelected();
    }

    private void OnFollowType(object sender, RoutedEventArgs e)
    {
        Editor.Show(_type, _length, _height,
            Inner(CurtainLayout.Build(_type, _length, _height, null, null).Verticals),
            Inner(CurtainLayout.Build(_type, _length, _height, null, null).Horizontals),
            Array.Empty<CurtainPanelOverride>());
        _followType = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // A grid only becomes the wall's own once a line has been changed; otherwise it keeps
        // following the type, and stretching the wall re-spaces it.
        var ownGrid = Editor.GridEdited || (_hadOwnGrid && !_followType);
        ResultGrid = ownGrid ? new CurtainGrid(Editor.Verticals.ToList(), Editor.Horizontals.ToList()) : null;
        ResultPanels = Editor.Panels.Count == 0 ? null : Editor.Panels;
        DialogResult = true;
    }
}
