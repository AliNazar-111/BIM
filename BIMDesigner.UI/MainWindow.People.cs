using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using DelegateCommand = BIMDesigner.Core.Views.DelegateCommand;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// People in the model who walk and see: walking the 3D view as an adult, a child or a wheelchair
/// user - stopped where they would be - and walking as a person placed in the model, through their
/// eyes, who is left standing wherever the walk ends.
/// </summary>
public partial class MainWindow
{
    // The person being walked as, and where they stood when the walk began.
    private Guid? _walkingPerson;

    private static readonly string[] WalkerChoices = { "Walk as an adult", "Walk as a child", "As a wheelchair user" };

    private void FillWalkAsPicker()
    {
        if (WalkAsPicker.ItemsSource is not null) return;
        WalkAsPicker.ItemsSource = WalkerChoices;
        WalkAsPicker.SelectedIndex = 0;
    }

    private void OnWalkAsChanged(object sender, RoutedEventArgs e)
    {
        var kind = WalkAsPicker.SelectedIndex switch { 1 => WalkerKind.Child, 2 => WalkerKind.WheelchairUser, _ => WalkerKind.Adult };
        Model3D.Walker = Walker.For(kind, _document.ProjectInformation.ClearanceStandard.IsUs());
        _walkingPerson = null;
    }

    /// <summary>Walk As Them: the 3D view from the picked person's eyes, walked as they can walk; they go where you go.</summary>
    private void OnWalkAsThem(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements is not [Component person] || _document.FindType<ComponentType>(person.TypeId) is not { } type || !Walker.IsPerson(type))
        {
            StatusHint.Text = "Select one person to walk as them.";
            return;
        }

        if (ModelPanel.Visibility != Visibility.Visible) Show3D();
        var walker = Walker.Of(type, _document.ProjectInformation.ClearanceStandard.IsUs());
        Model3D.Walker = walker;
        Model3D.WalkingAs = person.Id;
        var feet = ComponentHosting.BaseElevation(_document, person, type);
        if (Model3D.StartWalkingAt(person.Location, feet, person.Rotation - 90) is { } why)
        {
            StatusHint.Text = why;
            return;
        }

        _walkingPerson = person.Id;
        StatusHint.Text = $"Walking as {type.Name}, eyes {walker.EyeHeight:0} up: W A S D or the arrows, drag to look round. They go where you go - Esc stops, and leaves them there.";
    }

    /// <summary>When a walk as someone ends, they are left standing where it ended, facing the way it faced: one step to undo.</summary>
    private void LeavePersonWhereWalked()
    {
        if (Model3D.IsWalking || _walkingPerson is not { } id || Model3D.LastWalk is not var (feet, heading)) return;
        _walkingPerson = null;
        Model3D.WalkingAs = null;
        if (_document.FindElement(id) is not Component person || _document.FindType<ComponentType>(person.TypeId) is not { } type) return;

        var at = new Point2D(feet.X, feet.Y);
        if (at.DistanceTo(person.Location) < 50) return;

        // On whichever storey they ended up on, standing on the floor there.
        var level = _document.Levels.Where(each => each.Elevation <= feet.Z + 50).OrderByDescending(each => each.Elevation).FirstOrDefault()
                    ?? _document.Levels.OrderBy(each => each.Elevation).First();
        var slab = FloorSurfaces.FloorUnder(_document, level.Id, at);
        var underfoot = slab is not null ? slab.GetTopElevation(_document) : level.Elevation;
        var before = (person.Location, person.Rotation, person.LevelId, person.Elevation, person.StandsOn);
        var after = (at, ((heading + 90) % 360 + 360) % 360, level.Id, Math.Max(0, feet.Z - underfoot), slab?.Id);
        void Put((Point2D Location, double Rotation, Guid Level, double Elevation, Guid? StandsOn) state) =>
            (person.Location, person.Rotation, person.LevelId, person.Elevation, person.StandsOn) = state;

        _history.Execute(new DelegateCommand($"Walk {type.Name}", () => Put(after), () => Put(before)));
        AfterDataChange();
        StatusHint.Text = $"{type.Name} left where the walk ended, {Core.Units.Metres(person.Location.DistanceTo(before.Location), "0.0")} from where they stood{(level.Id != before.LevelId ? $", on {level.Name}" : string.Empty)}.";
    }
}
