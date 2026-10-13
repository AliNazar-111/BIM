using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;
using PartFinish = BIMDesigner.Core.Views.ComponentModel.PartFinish;

namespace BIMDesigner.UI;

/// <summary>
/// A small family editor: a family of your own drawn from simple solids - boxes, cylinders,
/// discs, spheres, cones, wedges - each placed and sized in millimetres in the family's own
/// frame and finished in its colour or a fixed finish; or loaded from a 3D model, an OBJ or STL
/// file, stood up and turned round as it needs. Its box is its size: what it takes in plan,
/// what it backs onto a wall with - and what it stretches with when a type of it is made bigger.
///
/// The preview is the 3D view itself, showing the family alone, one piece to each solid, so a
/// solid is picked by clicking it. Nothing changes in the project until OK, and then as one step.
/// </summary>
public partial class FamilyEditorWindow : System.Windows.Window
{
    private readonly BimDocument _document;
    private readonly ComponentType? _editing;
    private readonly FamilyDefinition _family;
    private readonly BimDocument _preview = new();
    private readonly Dictionary<Guid, int> _solidOf = new();
    private readonly bool _loadAtStart;
    private bool _loading;

    /// <summary>The family as edited: given to the type, or made a new one, by whoever opened this.</summary>
    public FamilyDefinition Family => _family;

    /// <param name="type">The family to edit, or none for a new one.</param>
    /// <param name="loadModel">Whether to ask for a 3D model to load as soon as it opens.</param>
    public FamilyEditorWindow(BimDocument document, ComponentType? type, bool loadModel = false)
    {
        InitializeComponent();
        ScreenFit.Apply(this);

        _document = document;
        _editing = type;
        _loadAtStart = loadModel;

        if (type is null)
        {
            _family = new FamilyDefinition
            {
                Name = TypeNames.Unique(document, loadModel ? "Model" : "My Family"),
                Kind = BuiltInCategory.Furniture,
                Width = 600, Depth = 600, Height = 600,
                Solids = loadModel ? new List<ComponentSolid>() : new List<ComponentSolid>
                {
                    new() { Name = "Box 1", From = -0.5, To = 0.5, Back = -0.5, Front = 0.5, Bottom = 0, Top = 1 }
                }
            };
            Title = "Family Editor - New Family";
        }
        else
        {
            _family = FamilyDefinition.Of(type);
            Title = $"Family Editor - {type.Name}";

            // One the program draws starts as the solids it is drawn with.
            if (type.Form != ComponentForm.Custom)
            {
                _family.Form = ComponentForm.Custom;
                _family.Solids = Families.SolidsOf(type);
                Note.Text = $"{type.Name} is drawn by the program as {EnumText.Humanise(type.Form).ToLowerInvariant()}. Here it is the {_family.Solids.Count} solids it is drawn with; " +
                            "on OK it is drawn with these instead - they stretch with its size, but no longer re-plan themselves as it changes.";
            }
        }

        _loading = true;
        CategoryPicker.ItemsSource = ComponentCategories.All.Select(category => EnumText.Humanise(category)).ToList();
        PlacementPicker.ItemsSource = EnumText.Choices<ComponentPlacement>();
        ShapePicker.ItemsSource = Enum.GetValues<SolidShape>().Select(shape => EnumText.Humanise(shape)).ToList();
        FinishPicker.ItemsSource = Enum.GetValues<PartFinish>().Select(FinishName).ToList();
        UnitsPicker.ItemsSource = Enum.GetValues<ModelUnits>().Select(units => EnumText.Humanise(units)).ToList();
        UnitsPicker.SelectedIndex = 0;
        _loading = false;

        Preview.ElementClicked += (_, hit) =>
        {
            if (hit is { } clicked && _solidOf.TryGetValue(clicked.Id, out var index)) SolidList.SelectedIndex = index;
        };

        ShowFamily();
        RefreshSolids(_family.Solids.Count > 0 ? 0 : -1);

        Loaded += (_, _) =>
        {
            Look("3D");
            if (_loadAtStart) OnLoadModel(this, new RoutedEventArgs());
        };
    }

    private static string FinishName(PartFinish finish) => finish == PartFinish.Body ? "Its own colour or material" : ComponentModel.Look(finish).Name;

    // ---- the family ---------------------------------------------------------------

    private void ShowFamily()
    {
        _loading = true;
        NameBox.Text = _family.Name;
        CategoryPicker.SelectedItem = EnumText.Humanise(_family.Kind);
        PlacementPicker.SelectedItem = EnumText.Humanise(_family.Placement);
        AgainstWallBox.IsChecked = _family.AgainstWall;
        WidthBox.Text = Length(_family.Width);
        DepthBox.Text = Length(_family.Depth);
        HeightBox.Text = Length(_family.Height);
        _loading = false;

        ModelText.Text = _family.Mesh is { } mesh
            ? $"{mesh.Source}: {mesh.TriangleCount:N0} triangles."
            : "None loaded. An OBJ or STL file's shape can be loaded, as well as or instead of solids.";
        StandUpButton.IsEnabled = TurnButton.IsEnabled = RemoveModelButton.IsEnabled = _family.Mesh is not null;
    }

    private void OnFamilyChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (CategoryPicker.SelectedItem is string category && EnumText.TryParse<BuiltInCategory>(category, out var kind)) _family.Kind = kind;
        if (PlacementPicker.SelectedItem is string placement && EnumText.TryParse<ComponentPlacement>(placement, out var how)) _family.Placement = how;
        _family.AgainstWall = AgainstWallBox.IsChecked == true;
    }

    /// <summary>Its box made another size: what it is drawn with stretches to it.</summary>
    private void OnSizeCommitted(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (ParseLength(WidthBox.Text) is { } width and > 0) _family.Width = width;
        if (ParseLength(DepthBox.Text) is { } depth and > 0) _family.Depth = depth;
        if (ParseLength(HeightBox.Text) is { } height and > 0) _family.Height = height;
        ShowFamily();
        ShowSolid();
        RefreshPreview();
    }

    private void OnFitSize(object sender, RoutedEventArgs e)
    {
        Families.FitSize(_family);
        ShowFamily();
        ShowSolid();
        RefreshPreview();
    }

    // ---- the solids ---------------------------------------------------------------

    private ComponentSolid? Picked => SolidList.SelectedIndex is var index and >= 0 && index < _family.Solids.Count ? _family.Solids[index] : null;

    private void RefreshSolids(int select)
    {
        _loading = true;
        SolidList.ItemsSource = _family.Solids.Select(solid => $"{solid.Name}  ·  {EnumText.Humanise(solid.Shape)}").ToList();
        SolidList.SelectedIndex = Math.Min(select, _family.Solids.Count - 1);
        _loading = false;
        ShowSolid();
        RefreshPreview();
    }

    private void OnSolidPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowSolid();
        SelectInPreview();
    }

    private void ShowSolid()
    {
        var solid = Picked;
        SolidPanel.IsEnabled = DuplicateSolidButton.IsEnabled = DeleteSolidButton.IsEnabled = solid is not null;
        if (solid is null)
        {
            _loading = true;
            foreach (var box in new[] { SolidNameBox, AcrossBox, ForwardBox, BottomBox, SolidWidthBox, SolidDepthBox, SolidHeightBox }) box.Text = string.Empty;
            ShapePicker.SelectedItem = FinishPicker.SelectedItem = null;
            _loading = false;
            return;
        }

        var part = solid.Part(_family.Width, _family.Depth, _family.Height);
        _loading = true;
        SolidNameBox.Text = solid.Name;
        ShapePicker.SelectedItem = EnumText.Humanise(solid.Shape);
        FinishPicker.SelectedItem = FinishName(solid.Finish);
        AcrossBox.Text = Length((part.From + part.To) / 2);
        ForwardBox.Text = Length((part.Back + part.Front) / 2);
        BottomBox.Text = Length(part.Bottom);
        SolidWidthBox.Text = Length(part.To - part.From);
        SolidDepthBox.Text = Length(part.Front - part.Back);
        SolidHeightBox.Text = Length(part.Top - part.Bottom);
        _loading = false;
    }

    private void OnSolidChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Picked is not { } solid) return;

        if (ShapePicker.SelectedItem is string shape && EnumText.TryParse<SolidShape>(shape, out var picked)) solid.Shape = picked;
        if (FinishPicker.SelectedIndex >= 0) solid.Finish = Enum.GetValues<PartFinish>()[FinishPicker.SelectedIndex];
        RefreshSolids(SolidList.SelectedIndex);
    }

    /// <summary>A solid's name, place or size typed in: where it is, in millimetres, made fractions of the family's box.</summary>
    private void OnSolidCommitted(object sender, RoutedEventArgs e)
    {
        if (_loading || Picked is not { } solid) return;

        var part = solid.Part(_family.Width, _family.Depth, _family.Height);
        var across = ParseLength(AcrossBox.Text) ?? (part.From + part.To) / 2;
        var forward = ParseLength(ForwardBox.Text) ?? (part.Back + part.Front) / 2;
        var bottom = ParseLength(BottomBox.Text) ?? part.Bottom;
        var width = ParseLength(SolidWidthBox.Text) is { } w and > 0 ? w : part.To - part.From;
        var depth = ParseLength(SolidDepthBox.Text) is { } d and > 0 ? d : part.Front - part.Back;
        var height = ParseLength(SolidHeightBox.Text) is { } h and > 0 ? h : part.Top - part.Bottom;

        solid.Name = string.IsNullOrWhiteSpace(SolidNameBox.Text) ? solid.Name : SolidNameBox.Text.Trim();
        solid.From = (across - width / 2) / _family.Width;
        solid.To = (across + width / 2) / _family.Width;
        solid.Back = (forward - depth / 2) / _family.Depth;
        solid.Front = (forward + depth / 2) / _family.Depth;
        solid.Bottom = bottom / _family.Height;
        solid.Top = (bottom + height) / _family.Height;

        RefreshSolids(SolidList.SelectedIndex);
    }

    private void OnAddSolid(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<SolidShape>(tag, out var shape)) return;

        var count = _family.Solids.Count(solid => solid.Shape == shape) + 1;
        _family.Solids.Add(new ComponentSolid { Name = $"{EnumText.Humanise(shape)} {count}", Shape = shape });
        RefreshSolids(_family.Solids.Count - 1);
    }

    private void OnDuplicateSolid(object sender, RoutedEventArgs e)
    {
        if (Picked is not { } solid) return;

        // Beside the one copied, so both can be seen.
        var copy = solid.Copy();
        var width = copy.To - copy.From;
        copy.From += width;
        copy.To += width;
        copy.Name = $"{solid.Name} copy";
        _family.Solids.Insert(SolidList.SelectedIndex + 1, copy);
        RefreshSolids(SolidList.SelectedIndex + 1);
    }

    private void OnDeleteSolid(object sender, RoutedEventArgs e)
    {
        if (Picked is not { } solid) return;

        var index = SolidList.SelectedIndex;
        _family.Solids.Remove(solid);
        RefreshSolids(Math.Max(0, index - 1));
    }

    // ---- the 3D model -------------------------------------------------------------

    private void OnLoadModel(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Load a 3D Model", Filter = ModelFiles.Filter };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var (corners, triangles, yUp) = ModelFiles.Read(dialog.FileName);
            var units = Enum.GetValues<ModelUnits>()[Math.Max(0, UnitsPicker.SelectedIndex)];
            if (ComponentMeshes.From(corners, triangles, units, yUp, Path.GetFileName(dialog.FileName)) is not var (mesh, width, depth, height))
            {
                MessageBox.Show(this, "There is nothing in that file to make a family of.", "Load a 3D Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // A new family named for the model it is.
            if (_editing is null && _family.Solids.Count == 0 && _family.Mesh is null)
            {
                _family.Name = TypeNames.Unique(_document, Path.GetFileNameWithoutExtension(dialog.FileName));
                Title = $"Family Editor - {_family.Name}";
            }

            Remodel(mesh, width, depth, height);

            var thinned = triangles.Count / 3 > ComponentMeshes.Budget ? $" It had {triangles.Count / 3:N0} triangles, and was thinned to {mesh.TriangleCount:N0} to keep the model quick." : string.Empty;
            Note.Text = $"{Path.GetFileName(dialog.FileName)} loaded, {Length(width)} x {Length(depth)} x {Length(height)} - if that is not its size, pick what it was drawn in and load it again. " +
                        $"If it is lying down, Stand Up; if it faces away, Turn Round.{thinned}";
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, failure.Message, "Load a 3D Model", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnStandUp(object sender, RoutedEventArgs e) => Turn(standUp: true);

    private void OnTurnRound(object sender, RoutedEventArgs e) => Turn(standUp: false);

    private void Turn(bool standUp)
    {
        if (_family.Mesh is not { } mesh) return;

        var (turned, width, depth, height) = ComponentMeshes.Turned(mesh, _family.Width, _family.Depth, _family.Height, standUp);
        Remodel(turned, width, depth, height);
    }

    private void OnRemoveModel(object sender, RoutedEventArgs e)
    {
        _family.Mesh = null;
        ShowFamily();
        RefreshPreview();
    }

    /// <summary>The family given a model, its box made the model's - its solids staying where they are.</summary>
    private void Remodel(ComponentMesh mesh, double width, double depth, double height)
    {
        foreach (var solid in _family.Solids)
        {
            var part = solid.Part(_family.Width, _family.Depth, _family.Height);
            (solid.From, solid.To) = (part.From / width, part.To / width);
            (solid.Back, solid.Front) = (part.Back / depth, part.Front / depth);
            (solid.Bottom, solid.Top) = (part.Bottom / height, part.Top / height);
        }

        (_family.Width, _family.Depth, _family.Height) = (width, depth, height);
        _family.Mesh = mesh;
        ShowFamily();
        ShowSolid();
        RefreshPreview();
        Preview.ZoomToFit();
    }

    // ---- the preview --------------------------------------------------------------

    /// <summary>The family alone in the preview: one piece to each solid, so each can be clicked on, and one for its model.</summary>
    private void RefreshPreview()
    {
        _preview.Elements.Clear();
        foreach (var type in _preview.ElementTypes.ToList()) _preview.RemoveType(type);
        _solidOf.Clear();

        if (_document.FindMaterial(_family.MaterialId) is { } material && _preview.FindMaterial(material.Id) is null) _preview.AddMaterial(material);

        ComponentType Piece(string name)
        {
            var type = new ComponentType(name, _family.Kind, ComponentForm.Custom, _family.Width, _family.Depth, _family.Height)
            {
                Colour = _family.Colour, MaterialId = _family.MaterialId
            };
            _preview.AddType(type);
            return type;
        }

        for (var i = 0; i < _family.Solids.Count; i++)
        {
            var type = Piece(_family.Solids[i].Name);
            type.Solids.Add(_family.Solids[i]);
            var piece = new Component { TypeId = type.Id, TypeKind = type.Kind };
            _preview.Add(piece);
            _solidOf[piece.Id] = i;
        }

        if (_family.Mesh is { } mesh)
        {
            var type = Piece("Model");
            type.Mesh = mesh;
            _preview.Add(new Component { TypeId = type.Id, TypeKind = type.Kind });
        }

        Preview.Document = _preview;
        Preview.Rebuild();
        SelectInPreview();
    }

    private void SelectInPreview() =>
        Preview.SetSelection(_solidOf.Where(entry => entry.Value == SolidList.SelectedIndex).Select(entry => entry.Key));

    private void OnLook(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string view }) Look(view);
    }

    /// <summary>The preview looked at from the front, the side, above, or in three quarters.</summary>
    private void Look(string view)
    {
        switch (view)
        {
            case "Front": Preview.LookFrom(-90, 0); break;
            case "Side": Preview.LookFrom(0, 0); break;
            case "Top": Preview.LookFrom(-90, Controls.ModelView.MaxPitch); break;
            default: Preview.LookFrom(-60, 25); break;
        }

        Preview.ZoomToFit();
    }

    private void OnFit(object sender, RoutedEventArgs e) => Preview.ZoomToFit();

    // ---- typing -------------------------------------------------------------------

    private void OnFieldKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;

        // Enter commits what was typed, as leaving the box does.
        box.RaiseEvent(new RoutedEventArgs(LostFocusEvent, box));
        box.SelectAll();
        e.Handled = true;
    }

    private static string Length(double mm) => Math.Round(mm, 1).ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>A length typed in: millimetres, or with "mm", "cm" or "m" after it.</summary>
    private static double? ParseLength(string text)
    {
        var trimmed = text.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        var scale = 1.0;
        if (trimmed.EndsWith("mm")) trimmed = trimmed[..^2];
        else if (trimmed.EndsWith("cm")) (trimmed, scale) = (trimmed[..^2], 10);
        else if (trimmed.EndsWith("m")) (trimmed, scale) = (trimmed[..^1], 1000);

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
               || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            ? double.IsFinite(value) ? value * scale : null
            : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // What was being typed counts.
        OnSizeCommitted(this, e);
        if (Picked is not null) OnSolidCommitted(this, e);

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Give the family a name.", "Family Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_document.ElementTypes.Any(type => type != _editing && string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"There is already a type called {name}. Give this one another name.", "Family Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_family.Solids.Count == 0 && _family.Mesh is null)
        {
            MessageBox.Show(this, "Draw it with a solid, or load a 3D model, first.", "Family Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _family.Name = name;
        _family.Form = ComponentForm.Custom;
        DialogResult = true;
    }
}
