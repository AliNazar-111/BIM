using System.Windows;

namespace BIMDesigner.UI.StartUp;

/// <summary>File > New Project from a project already open: the same steps as the home screen's, in a window of their own.</summary>
public sealed class NewProjectWindow : Window
{
    public NewProjectWindow()
    {
        DialogLook.Apply(this, "BIMDesigner - New Project", 1100, 720);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 900;
        MinHeight = 600;
        Steps = new NewProjectSteps { Margin = new Thickness(32, 24, 32, 20) };
        Steps.Created += (_, choice) =>
        {
            Choice = choice;
            DialogResult = true;
        };
        Steps.Cancelled += (_, _) => DialogResult = false;
        Content = Steps;
    }

    public NewProjectSteps Steps { get; }

    /// <summary>The project made, once Create Project is chosen.</summary>
    public StartChoice? Choice { get; private set; }
}
