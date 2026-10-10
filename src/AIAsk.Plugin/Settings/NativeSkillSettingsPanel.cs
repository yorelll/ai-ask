using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace AIAsk.Plugin.Settings;

/// <summary>
/// Native Flow settings control modeled after the project's reference images:
/// a DataGrid listing alias/path/global/status plus Add, Edit, and Delete.
/// The plugin shell should expose it from ISettingProvider.CreateSettingPanel.
/// </summary>
public sealed class NativeSkillSettingsPanel : UserControl
{
    private readonly SkillSettingsController _controller;
    private readonly ObservableCollection<SkillSettingsRow> _rows = [];
    private readonly DataGrid _grid;
    private readonly TextBlock _error;

    public NativeSkillSettingsPanel(SkillSettingsController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        var root = new DockPanel { Margin = new Thickness(12) };
        _error = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(_error, Dock.Top);
        root.Children.Add(_error);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        toolbar.Children.Add(CreateButton("Delete", (_, _) => DeleteSelected()));
        toolbar.Children.Add(CreateButton("Edit", (_, _) => EditSelected()));
        toolbar.Children.Add(CreateButton("Add", (_, _) => AddSkill()));
        DockPanel.SetDock(toolbar, Dock.Bottom);
        root.Children.Add(toolbar);

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = false,
            ItemsSource = _rows,
            SelectionMode = DataGridSelectionMode.Single,
            MinHeight = 240
        };
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Alias",
            Binding = new Binding(nameof(SkillSettingsRow.Alias)),
            IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Path",
            Binding = new Binding(nameof(SkillSettingsRow.Path)),
            IsReadOnly = true,
            Width = new DataGridLength(3, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Global",
            Binding = new Binding(nameof(SkillSettingsRow.IsGlobal))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            },
            Width = DataGridLength.Auto
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Status",
            Binding = new Binding(nameof(SkillSettingsRow.Status)),
            IsReadOnly = true,
            Width = DataGridLength.Auto
        });
        _grid.CellEditEnding += OnCellEditEnding;
        root.Children.Add(_grid);

        Content = root;
        Reload();
    }

    /// <summary>Factory used by the later ISettingProvider adapter.</summary>
    public static UserControl Create(SkillSettingsController controller) => new NativeSkillSettingsPanel(controller);

    public void Reload()
    {
        try
        {
            _rows.Clear();
            foreach (var row in _controller.LoadRows())
            {
                _rows.Add(row);
            }
            ClearError();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs args)
    {
        if (args.EditAction != DataGridEditAction.Commit ||
            args.Row.Item is not SkillSettingsRow row ||
            args.Column.Header?.ToString() != "Global")
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            var result = _controller.SetGlobal(row.Alias, row.IsGlobal);
            if (!result.IsSuccess)
            {
                ShowError(result.Error ?? "Unable to update global skill state.");
            }
            Reload();
        });
    }

    private void AddSkill()
    {
        var dialog = new SkillEditorDialog(_controller, existing: null)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            Reload();
        }
    }

    private void EditSelected()
    {
        if (_grid.SelectedItem is not SkillSettingsRow row)
        {
            ShowError("Select a skill to edit.");
            return;
        }

        var dialog = new SkillEditorDialog(_controller, row)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            Reload();
        }
    }

    private void DeleteSelected()
    {
        if (_grid.SelectedItem is not SkillSettingsRow row)
        {
            ShowError("Select a skill to delete.");
            return;
        }

        if (MessageBox.Show($"Delete skill '{row.Alias}'? The skill file will not be deleted.", "AI Ask Skills", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = _controller.Delete(row.Alias);
        if (!result.IsSuccess)
        {
            ShowError(result.Error ?? "Unable to delete skill.");
            return;
        }

        Reload();
    }

    private static Button CreateButton(string text, RoutedEventHandler action)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(4, 0, 0, 0),
            MinWidth = 84,
            Padding = new Thickness(8, 4, 8, 4)
        };
        button.Click += action;
        return button;
    }

    private void ShowError(string message) => _error.Text = message;

    private void ClearError() => _error.Text = string.Empty;
}

/// <summary>Native add/edit metadata dialog. Skill file contents are never edited.</summary>
internal sealed class SkillEditorDialog : Window
{
    private readonly SkillSettingsController _controller;
    private readonly SkillSettingsRow? _existing;
    private readonly TextBox _alias;
    private readonly TextBox _path;
    private readonly CheckBox _global;
    private readonly TextBlock _error;

    public SkillEditorDialog(SkillSettingsController controller, SkillSettingsRow? existing)
    {
        _controller = controller;
        _existing = existing;
        Title = existing is null ? "Add Skill" : "Edit Skill";
        Width = 520;
        Height = 315;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(Label("Alias", 0));
        _alias = Input(existing?.Alias ?? string.Empty, 1);
        grid.Children.Add(_alias);

        grid.Children.Add(Label("Skill file path", 2));
        var pathPanel = new DockPanel { LastChildFill = true };
        Grid.SetRow(pathPanel, 3);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        browse.Click += (_, _) => BrowseForPath();
        DockPanel.SetDock(browse, Dock.Right);
        pathPanel.Children.Add(browse);
        _path = new TextBox { Text = existing?.Path ?? string.Empty, MinWidth = 300, Padding = new Thickness(6, 4, 6, 4) };
        pathPanel.Children.Add(_path);
        grid.Children.Add(pathPanel);

        _global = new CheckBox { Content = "Load globally as system prompt", IsChecked = existing?.IsGlobal ?? false, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(_global, 4);
        grid.Children.Add(_global);

        _error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(_error, 4);
        Grid.SetColumn(_error, 1);
        grid.Children.Add(_error);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, Margin = new Thickness(4), IsCancel = true };
        var save = new Button { Content = "Save", MinWidth = 96, Margin = new Thickness(4), IsDefault = true };
        save.Click += (_, _) => Save();
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        Grid.SetRow(footer, 5);
        grid.Children.Add(footer);

        Content = grid;
    }

    private static TextBlock Label(string text, int row)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 3) };
        Grid.SetRow(label, row);
        return label;
    }

    private static TextBox Input(string value, int row)
    {
        var input = new TextBox { Text = value, Padding = new Thickness(6, 4, 6, 4) };
        Grid.SetRow(input, row);
        return input;
    }

    private void BrowseForPath()
    {
        var dialog = new OpenFileDialog { Filter = "Text/Markdown files|*.txt;*.md;*.markdown|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            _path.Text = dialog.FileName;
        }
    }

    private void Save()
    {
        var result = _existing is null
            ? _controller.Add(_alias.Text, _path.Text, _global.IsChecked == true)
            : _controller.Edit(_existing.Alias, _alias.Text, _path.Text, _global.IsChecked == true);
        if (!result.IsSuccess)
        {
            _error.Text = result.Error ?? "Unable to save skill.";
            return;
        }

        DialogResult = true;
    }
}
