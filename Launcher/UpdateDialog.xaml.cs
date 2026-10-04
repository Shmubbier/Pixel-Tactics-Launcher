using System.Windows;

namespace PtoLauncher;

// Shown on launch when a newer release exists. DialogResult true = user chose to update.
public partial class UpdateDialog : Window
{
    public UpdateDialog(string version, string changelog)
    {
        InitializeComponent();
        VersionText.Text = "Version " + version + " (you have " + LauncherInfo.LauncherVersion + ")";
        ChangelogBox.Text = string.IsNullOrWhiteSpace(changelog) ? "(no changelog provided)" : changelog;
    }

    void OnUpdate(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    void OnLater(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
