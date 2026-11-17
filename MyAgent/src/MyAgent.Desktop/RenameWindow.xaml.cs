using System.Windows;
using System.Windows.Input;

namespace MyAgent.Desktop;

public partial class RenameWindow : Window
{
    public string NewTitle { get; private set; } = "";

    public RenameWindow(string currentTitle)
    {
        InitializeComponent();
        TitleBox.Text = currentTitle;
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Save();
    }

    private void Save()
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) return;
        NewTitle = title;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}