using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace AIAsk.Plugin.Settings;

/// <summary>
/// Root Flow-native settings panel. It persists API controls using
/// IPluginSettingsAccessor and hosts the dynamic WPF skill DataGrid.
/// Main.CreateSettingPanel should construct this control through Create().
/// </summary>
public sealed class NativeSkillSettingsPanel : UserControl
{
    private readonly SkillSettingsController _skillController;
    private readonly IPluginSettingsAccessor _pluginSettings;
    private readonly ObservableCollection<SkillSettingsRow> _rows = [];
    private readonly DataGrid _grid;
    private readonly TextBlock _error;
    private TextBox _baseUrl = null!;
    private PasswordBox _apiKey = null!;
    private TextBox _model = null!;
    private TextBox _maxTokens = null!;
    private TextBox _timeout = null!;

    public NativeSkillSettingsPanel(
        SkillSettingsController skillController,
        IPluginSettingsAccessor pluginSettings)
    {
        _skillController = skillController ?? throw new ArgumentNullException(nameof(skillController));
        _pluginSettings = pluginSettings ?? throw new ArgumentNullException(nameof(pluginSettings));

        var settings = _pluginSettings.Current;
        FlowPluginSettingsAccessor.Normalize(settings);

        var root = new DockPanel { Margin = new Thickness(12) };
        var title = new TextBlock
        {
            Text = "AI Ask Settings",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        _error = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(_error, Dock.Top);
        root.Children.Add(_error);

        var apiPanel = CreateApiPanel(settings);
        DockPanel.SetDock(apiPanel, Dock.Top);
        root.Children.Add(apiPanel);

        var skillHeader = new TextBlock
        {
            Text = "Skills",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 6)
        };
        DockPanel.SetDock(skillHeader, Dock.Top);
        root.Children.Add(skillHeader);

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

        _grid = CreateGrid();
        root.Children.Add(_grid);

        Content = root;
        ReloadSkills();
    }

    /// <summary>
    /// Public factory for Main.CreateSettingPanel. The shell must create the
    /// FlowPluginSettingsAccessor from context.API during InitAsync.
    /// </summary>
    public static UserControl Create(
        SkillSettingsController skillController,
        IPluginSettingsAccessor pluginSettings) =>
        new NativeSkillSettingsPanel(skillController, pluginSettings);

    // Internal test seams: the UI behavior is otherwise exercised on STA in CI.
    internal DataGrid SkillsGrid => _grid;

    internal IReadOnlyList<SkillSettingsRow> SkillRows => _rows;

    public void ReloadSkills()
    {
        try
        {
            _rows.Clear();
            foreach (var row in _skillController.LoadRows())
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

    private Grid CreateApiPanel(AIAskPluginSettings settings)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 5; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        _baseUrl = AddInput(grid, "Base URL", settings.BaseUrl, 0);
        _apiKey = AddPassword(grid, "API Key", settings.ApiKey, 1);
        _model = AddInput(grid, "Model", settings.Model, 2);
        _maxTokens = AddInput(grid, "Max Token", settings.MaxTokens.ToString(), 3);
        _timeout = AddInput(grid, "Timeout (seconds)", settings.TimeoutSeconds.ToString(), 4);

        _baseUrl.LostFocus += (_, _) => SaveApiSettings();
        _apiKey.LostFocus += (_, _) => SaveApiSettings();
        _model.LostFocus += (_, _) => SaveApiSettings();
        _maxTokens.LostFocus += (_, _) => SaveApiSettings();
        _timeout.LostFocus += (_, _) => SaveApiSettings();
        return grid;
    }

    /// <summary>
    /// File-manager inspired Skill list: single full-row selection, no current
    /// cell focus visuals, a closed outer border, and a left selected-row bar.
    /// The style belongs to the DataGrid row/container—not individual cells.
    /// </summary>
    private DataGrid CreateGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = true,
            ItemsSource = _rows,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            BorderBrush = Brush("#E2E2E2"),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            RowBackground = Brushes.White,
            AlternatingRowBackground = Brushes.White,
            HorizontalGridLinesBrush = Brush("#ECECEC"),
            VerticalGridLinesBrush = Brushes.Transparent,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            MinHeight = 240
        };
        grid.Resources.Add(typeof(DataGridCell), CreateCellStyle());
        grid.Resources.Add(typeof(DataGridRow), CreateRowStyle());
        grid.Resources.Add(typeof(DataGridColumnHeader), CreateHeaderStyle());
        grid.PreviewMouseLeftButtonDown += SelectRowBeforeCellFocus;
        grid.Columns.Add(CreateTextColumn("Alias", nameof(SkillSettingsRow.Alias), 1));
        grid.Columns.Add(CreateTextColumn("Path", nameof(SkillSettingsRow.Path), 3));
        grid.Columns.Add(CreateGlobalColumn());
        grid.Columns.Add(CreateTextColumn("Status", nameof(SkillSettingsRow.Status), width: null));
        return grid;
    }

    private DataGridTextColumn CreateTextColumn(string header, string property, double? width)
    {
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property),
            IsReadOnly = true,
            Width = width is null ? DataGridLength.Auto : new DataGridLength(width.Value, DataGridLengthUnitType.Star),
            ElementStyle = TransparentTextStyle()
        };
    }

    private DataGridTemplateColumn CreateGlobalColumn()
    {
        var column = new DataGridTemplateColumn { Header = "Global", Width = DataGridLength.Auto };
        var template = new DataTemplate();
        var checkBox = new FrameworkElementFactory(typeof(CheckBox));
        checkBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(SkillSettingsRow.IsGlobal))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        checkBox.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        checkBox.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        checkBox.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(OnGlobalCheckBoxClick));
        template.VisualTree = checkBox;
        column.CellTemplate = template;
        return column;
    }

    private void SelectRowBeforeCellFocus(object sender, MouseButtonEventArgs args)
    {
        if (FindParent<DataGridRow>(args.OriginalSource as DependencyObject) is { } row)
        {
            _grid.SelectedItem = row.Item;
        }
    }

    private void OnGlobalCheckBoxClick(object sender, RoutedEventArgs args)
    {
        if (FindParent<DataGridRow>(sender as DependencyObject) is not { Item: SkillSettingsRow row })
        {
            return;
        }

        _grid.SelectedItem = row;
        Dispatcher.BeginInvoke(() =>
        {
            var result = _skillController.SetGlobal(row.Alias, row.IsGlobal);
            if (!result.IsSuccess)
            {
                ShowError(result.Error ?? "Unable to update global skill state.");
            }
            ReloadSkills();
        });
    }

    /// <summary>
    /// Explicitly replaces the theme's DataGridCell template. Setter-only
    /// overrides are not sufficient because the host Flow WPF theme can add
    /// IsSelected/current-cell triggers that paint one cell darker than the
    /// selected row. This template deliberately has no such triggers: cells
    /// stay transparent and the DataGridRow owns all selection visuals.
    /// </summary>
    private static Style CreateCellStyle()
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(IsTabStopProperty, false));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 0, 12, 0)));
        style.Setters.Add(new Setter(TemplateProperty, CreateCellTemplate()));
        return style;
    }

    private static ControlTemplate CreateCellTemplate()
    {
        const string markup = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type DataGridCell}">
              <Border Background="Transparent" BorderBrush="Transparent" BorderThickness="0"
                      Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                <ContentPresenter Content="{TemplateBinding Content}"
                                  ContentTemplate="{TemplateBinding ContentTemplate}"
                                  ContentTemplateSelector="{TemplateBinding ContentTemplateSelector}"
                                  ContentStringFormat="{TemplateBinding ContentStringFormat}"
                                  VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                  HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" />
              </Border>
            </ControlTemplate>
            """;
        return (ControlTemplate)XamlReader.Parse(markup);
    }

    private static Style CreateRowStyle()
    {
        var style = new Style(typeof(DataGridRow));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.White));
        style.Setters.Add(new Setter(ForegroundProperty, Brush("#222222")));
        style.Setters.Add(new Setter(BorderBrushProperty, Brush("#ECECEC")));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(TemplateProperty, CreateRowTemplate()));
        return style;
    }

    private static ControlTemplate CreateRowTemplate()
    {
        const string markup = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type DataGridRow}">
              <Border x:Name="RowBorder" Background="{TemplateBinding Background}"
                      BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
                <Grid>
                  <Border x:Name="SelectionBar" Width="3" Background="#A87520" HorizontalAlignment="Left" Visibility="Collapsed" />
                  <DataGridCellsPresenter />
                </Grid>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="RowBorder" Property="Background" Value="#F7F7F7" />
                </Trigger>
                <Trigger Property="IsSelected" Value="True">
                  <Setter TargetName="RowBorder" Property="Background" Value="#F1F1F1" />
                  <Setter TargetName="SelectionBar" Property="Visibility" Value="Visible" />
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """;
        return (ControlTemplate)XamlReader.Parse(markup);
    }

    private static Style CreateHeaderStyle()
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(BackgroundProperty, Brush("#FAFAFA")));
        style.Setters.Add(new Setter(ForegroundProperty, Brush("#666666")));
        style.Setters.Add(new Setter(BorderBrushProperty, Brush("#E2E2E2")));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 8, 12, 8)));
        return style;
    }

    private static Style TransparentTextStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(ForegroundProperty, Brush("#222222")));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        return style;
    }

    private static Brush Brush(string value) =>
        (Brush)new BrushConverter().ConvertFromString(value)!;

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T parent)
            {
                return parent;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void SaveApiSettings()
    {
        if (!int.TryParse(_maxTokens.Text, out var maxTokens) || maxTokens <= 0)
        {
            ShowError("Max Token must be a positive integer.");
            return;
        }
        if (!int.TryParse(_timeout.Text, out var timeout) || timeout <= 0)
        {
            ShowError("Timeout must be a positive integer.");
            return;
        }

        var settings = _pluginSettings.Current;
        settings.BaseUrl = _baseUrl.Text;
        settings.ApiKey = _apiKey.Password;
        settings.Model = _model.Text;
        settings.MaxTokens = maxTokens;
        settings.TimeoutSeconds = timeout;
        _pluginSettings.Save();
        ClearError();
    }

    private void AddSkill()
    {
        var dialog = new SkillEditorDialog(_skillController, existing: null)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            ReloadSkills();
        }
    }

    private void EditSelected()
    {
        if (_grid.SelectedItem is not SkillSettingsRow row)
        {
            ShowError("Select a skill to edit.");
            return;
        }

        var dialog = new SkillEditorDialog(_skillController, row)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            ReloadSkills();
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

        var result = _skillController.Delete(row.Alias);
        if (!result.IsSuccess)
        {
            ShowError(result.Error ?? "Unable to delete skill.");
            return;
        }
        ReloadSkills();
    }

    private static TextBox AddInput(Grid grid, string labelText, string value, int row)
    {
        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 5, 12, 5)
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var input = new TextBox { Text = value, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(6, 4, 6, 4) };
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        return input;
    }

    private static PasswordBox AddPassword(Grid grid, string labelText, string value, int row)
    {
        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 5, 12, 5)
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var input = new PasswordBox { Password = value, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(6, 4, 6, 4) };
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        return input;
    }

    private static Button CreateButton(string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text, Margin = new Thickness(4, 0, 0, 0), MinWidth = 84, Padding = new Thickness(8, 4, 8, 4) };
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
        for (var index = 0; index < 6; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = index == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }

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
