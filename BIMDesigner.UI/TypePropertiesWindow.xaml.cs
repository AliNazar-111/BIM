using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.UI.ViewModels;

namespace BIMDesigner.UI;

/// <summary>
/// Type Properties, as Revit has it, for a component, door, window, chimney, gutter or the like:
/// the family and the type, Duplicate to make a type of your own from it - another size, another
/// material - given to what is selected and to nothing else; Rename; Delete one nothing is
/// placed from; and the type's parameters, which every piece of it shares. Edits are made as
/// they are typed, each one a step to undo.
/// </summary>
public partial class TypePropertiesWindow : Window
{
    private readonly BimDocument _document;
    private readonly UndoStack _history;
    private readonly IReadOnlyList<Element> _selection;
    private ElementType _type;
    private bool _loading;

    /// <summary>Raised after any change, so the views behind follow it.</summary>
    public event EventHandler? Changed;

    /// <param name="selection">What it was opened for: what Duplicate and a type picked here are given to.</param>
    public TypePropertiesWindow(BimDocument document, UndoStack history, ElementType type, IReadOnlyList<Element> selection)
    {
        InitializeComponent();
        ScreenFit.Apply(this);

        _document = document;
        _history = history;
        _selection = selection.Where(element => element.Category == type.Category).ToList();
        _type = type;
        Show(type);
    }

    /// <summary>The type being edited now.</summary>
    public ElementType Type => _type;

    private void Show(ElementType type)
    {
        _type = type;
        _loading = true;

        FamilyText.Text = EnumText.Humanise(type.Category);
        var types = _document.ElementTypes
            .Where(candidate => candidate.Category == type.Category && candidate.GetType() == type.GetType())
            .OrderBy(candidate => candidate.Name)
            .ToList();
        TypePicker.ItemsSource = types;
        TypePicker.SelectedItem = type;
        _loading = false;

        // A mullion type is placed in the curtain walls framed with it.
        var placed = type is Core.Architecture.MullionType mullion
            ? _document.Walls.Count(wall => Core.Architecture.CurtainLayout.Of(_document, wall)?.Mullions.Any(m => m.Type == mullion) == true)
            : _document.ElementsOfType(type).Count();
        Hint.Text = placed switch
        {
            0 => "Nothing is placed from this type yet.",
            1 => "One piece is placed from this type: a change here changes it.",
            _ => $"{placed} pieces are placed from this type: a change here changes them all. To change only the selected ones, Duplicate first - it makes a type of your own and gives it to them."
        };
        DeleteButton.IsEnabled = placed == 0 && types.Count > 1 &&
                                 !(type is Core.Architecture.MullionType framed && _document.TypesOf<Core.Architecture.CurtainWallType>().Any(curtain => curtain.MullionTypes(_document).Contains(framed)));
        EditFamilyButton.Visibility = type is Core.Architecture.ComponentType ? Visibility.Visible : Visibility.Collapsed;

        // In the order Revit lists them: constraints, dimensions, construction, materials, then identity.
        var rows = new List<ParameterRow>();
        foreach (var parameter in type.AllTypeParameters(_document).OrderBy(parameter => (int)parameter.Group))
        {
            var row = new ParameterRow(parameter);
            row.ValueCommitted += OnCommitted;
            row.Explained += (_, said) =>
            {
                if (said.Refused) MessageBox.Show(this, said.Message, said.Parameter, MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            rows.Add(row);
        }

        var view = new CollectionViewSource { Source = rows }.View;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ParameterRow.GroupName)));
        Parameters.ItemsSource = view;
    }

    private void OnCommitted(object? sender, ParameterCommittedEventArgs e)
    {
        var commands = e.Changes.Select(change => new ParameterChangeCommand(change.Parameter, change.OldValue, change.NewValue)).ToList();
        _history.Record(commands.Count == 1 ? commands[0] : new CompositeCommand(commands[0].Name, commands));
        Changed?.Invoke(this, EventArgs.Empty);

        // A renamed type, or one whose size changes what else it shows, is shown afresh - after the edit is done.
        Dispatcher.BeginInvoke(() => Show(_type));
    }

    /// <summary>Another type picked: edited here - and, with something selected, given to it.</summary>
    private void OnTypePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TypePicker.SelectedItem is not ElementType picked || picked == _type) return;

        var given = _selection.Where(element => element.TypeId != picked.Id).ToList();
        if (given.Count > 0)
        {
            _history.Execute(new Core.Architecture.StairRelaidCommand(_document, given.OfType<Core.Architecture.Stair>(), new SetElementsTypeCommand(given, picked)));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        Show(picked);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        var suggested = TypeNames.Unique(_document, _type.Name);
        if (NamePrompt.Ask(this, "Duplicate", "Name of the new type:", suggested) is not { } name) return;

        if (TypeDuplicates.For(_document, _type, name, _selection) is not var (command, copy))
        {
            MessageBox.Show(this, $"{_type.Name} cannot be duplicated here.", "Duplicate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _history.Execute(command);
        Changed?.Invoke(this, EventArgs.Empty);
        Show(copy);
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (NamePrompt.Ask(this, "Rename", "New name:", _type.Name) is not { } name || name == _type.Name) return;

        if (_document.ElementTypes.Any(other => other != _type && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"There is already a type called {name}.", "Rename", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _history.Execute(new RenameTypeCommand(_type, name));
        Changed?.Invoke(this, EventArgs.Empty);
        Show(_type);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_document.ElementsOfType(_type).Any())
        {
            MessageBox.Show(this, $"{_type.Name} is in use and cannot be deleted.", "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var next = _document.ElementTypes.Where(other => other != _type && other.Category == _type.Category && other.GetType() == _type.GetType())
            .OrderBy(other => other.Name).FirstOrDefault();
        if (next is null) return;

        _history.Execute(new DeleteTypeCommand(_document, _type));
        Changed?.Invoke(this, EventArgs.Empty);
        Show(next);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>The family opened in the family editor; what comes back, one step to undo.</summary>
    private void OnEditFamily(object sender, RoutedEventArgs e)
    {
        if (_type is not Core.Architecture.ComponentType type) return;

        var editor = new FamilyEditorWindow(_document, type) { Owner = this };
        if (editor.ShowDialog() != true) return;

        _history.Execute(new Core.Architecture.EditFamilyCommand(_document, type, editor.Family));
        Changed?.Invoke(this, EventArgs.Empty);
        Show(type);
    }
}

/// <summary>A small window asking for one name, as Revit's Duplicate and Rename do.</summary>
public static class NamePrompt
{
    /// <summary>The name typed, trimmed, or null when cancelled or left empty.</summary>
    public static string? Ask(Window owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(4, 3, 4, 3), MinWidth = 320 };
        var ok = new Button { Content = "OK", Padding = new Thickness(20, 5, 20, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(box);
        content.Children.Add(buttons);

        var window = new Window
        {
            Title = title, Content = content, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = owner.Background, Foreground = owner.Foreground
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }
}
