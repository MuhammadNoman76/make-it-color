using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MakeItColor.Views;

public enum DialogIcon { None, Info, Warning, Error, Question, Success }

public enum DialogChoice { Primary, Secondary, Close }

/// <summary>A Windows 11 style message box: heading, text, optional details and up to three buttons.</summary>
public partial class MessageDialog
{
    private DialogChoice _choice = DialogChoice.Close;

    private MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>The state of the optional check box after the dialog closed.</summary>
    public bool OptionChecked => Option.IsChecked == true;

    public static DialogChoice Show(Window? owner, string heading, string message, string primary, string? secondary = null,
        string? close = null, DialogIcon icon = DialogIcon.None, string? details = null, string? title = null)
    {
        var dialog = Create(owner, heading, message, primary, secondary, close, icon, details, title);
        dialog.ShowDialog();
        return dialog._choice;
    }

    /// <summary>Like <see cref="Show"/> with a check box ("Don't ask again"); returns the choice and the box.</summary>
    public static (DialogChoice Choice, bool Option) ShowWithOption(Window? owner, string heading, string message, string option, bool optionChecked,
        string primary, string? secondary = null, string? close = null, DialogIcon icon = DialogIcon.None)
    {
        var dialog = Create(owner, heading, message, primary, secondary, close, icon, null, null);
        dialog.Option.Content = option;
        dialog.Option.IsChecked = optionChecked;
        dialog.Option.Visibility = Visibility.Visible;
        dialog.ShowDialog();
        return (dialog._choice, dialog.OptionChecked);
    }

    private static MessageDialog Create(Window? owner, string heading, string message, string primary, string? secondary,
        string? close, DialogIcon icon, string? details, string? title)
    {
        var dialog = new MessageDialog();
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (title != null) dialog.Title = title;
        dialog.Heading.Text = heading;
        dialog.Message.Text = message;
        dialog.Message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(details))
        {
            dialog.Details.Text = details;
            dialog.DetailsBox.Visibility = Visibility.Visible;
        }

        dialog.PrimaryButton.Content = primary;
        dialog.SecondaryButton.Content = secondary;
        dialog.CloseButton.Content = close;
        dialog.SecondaryButton.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
        dialog.CloseButton.Visibility = close == null ? Visibility.Collapsed : Visibility.Visible;
        if (close == null) dialog.PrimaryButton.IsCancel = secondary == null;

        // Buttons share the footer evenly (like WinUI's ContentDialog); a lone button sits on the right half.
        var buttons = new List<Button> { dialog.PrimaryButton };
        if (secondary != null) buttons.Add(dialog.SecondaryButton);
        if (close != null) buttons.Add(dialog.CloseButton);
        var grid = dialog.Buttons;
        grid.ColumnDefinitions.Clear();
        grid.Children.Clear();
        var slots = Math.Max(2, buttons.Count);
        for (var i = 0; i < slots; i++)
        {
            if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var offset = slots - buttons.Count;
        for (var i = 0; i < buttons.Count; i++)
        {
            Grid.SetColumn(buttons[i], (i + offset) * 2);
            grid.Children.Add(buttons[i]);
        }

        var (geometry, brushKey) = icon switch
        {
            DialogIcon.Info => ("Icon.InfoFilled", "Accent.Fill"),
            DialogIcon.Warning => ("Icon.WarningFilled", "Status.Caution"),
            DialogIcon.Error => ("Icon.ErrorCircleFilled", "Status.Critical"),
            DialogIcon.Question => ("Icon.QuestionCircle", "Accent.Fill"),
            DialogIcon.Success => ("Icon.CheckmarkCircleFilled", "Status.Success"),
            _ => (null, null),
        };
        if (geometry == null)
        {
            dialog.Glyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.Glyph.Data = (Geometry)dialog.FindResource(geometry);
            dialog.Glyph.SetResourceReference(Controls.IconView.ForegroundProperty, brushKey);
        }
        return dialog;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Primary;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Secondary;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Close;
        Close();
    }
}
