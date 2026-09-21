using System.ComponentModel;
using System.Runtime.CompilerServices;
using BIMDesigner.Core;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.ViewModels;

/// <summary>
/// One row of the levels table.
///
/// It edits the real <see cref="Level"/> through the undo stack rather than holding a copy to
/// be applied later: a level is a datum the whole model hangs from, and an editor where the
/// building only catches up when you press OK would show you a plan that is not the plan.
/// </summary>
public sealed class LevelRow : INotifyPropertyChanged
{
    private readonly BimDocument _document;

    public LevelRow(BimDocument document, Level level)
    {
        _document = document;
        Level = level;
    }

    public Level Level { get; }

    /// <summary>Raised when an edit needs to become an undoable command. Handled by the window.</summary>
    public event EventHandler<LevelEditEventArgs>? Edited;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => Level.Name;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed == Level.Name) return;

            // A duplicate or blank name is rejected outright: levels are referred to by name
            // on drawings and in schedules, so two the same is a defect, not an annoyance.
            if (!Levels.IsNameAvailable(_document, trimmed, Level))
            {
                Refresh();
                return;
            }

            Edited?.Invoke(this, LevelEditEventArgs.Rename(Level, trimmed));
            Refresh();
        }
    }

    /// <summary>Shown and accepted in the same forms as every other length in the app.</summary>
    public string Elevation
    {
        get => Units.FormatLength(Level.Elevation);
        set
        {
            if (!ParameterFormatter.TryParse(ParameterDataType.Length, value, out var parsed) ||
                parsed is not double millimetres)
            {
                Refresh();
                return;
            }

            if (Math.Abs(millimetres - Level.Elevation) < 1e-6) return;

            Edited?.Invoke(this, LevelEditEventArgs.Move(Level, millimetres));
            Refresh();
        }
    }

    /// <summary>What would go with this level if it were deleted.</summary>
    public string Contents
    {
        get
        {
            var count = Levels.ElementsOn(_document, Level.Id).Count;
            return count switch
            {
                0 => "empty",
                1 => "1 element",
                _ => $"{count} elements"
            };
        }
    }

    public void Refresh()
    {
        Notify(nameof(Name));
        Notify(nameof(Elevation));
        Notify(nameof(Contents));
    }

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed class LevelEditEventArgs : EventArgs
{
    private LevelEditEventArgs(Level level, string? name, double? elevation)
    {
        Level = level;
        NewName = name;
        NewElevation = elevation;
    }

    public Level Level { get; }

    public string? NewName { get; }

    public double? NewElevation { get; }

    public static LevelEditEventArgs Rename(Level level, string name) => new(level, name, null);

    public static LevelEditEventArgs Move(Level level, double elevation) => new(level, null, elevation);
}
