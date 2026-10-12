using System.Threading;
using System.Windows.Controls;
using AIAsk.Plugin.Settings;
using AIAsk.Plugin.Skills;
using Xunit;

namespace AIAsk.Plugin.Tests.Settings;

public sealed class NativeSkillSettingsPanelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIAsk.GridStyleTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SkillsGrid_UsesFullRowSelectionAndClosedOuterBorder()
    {
        var details = RunOnSta(() =>
        {
            var panel = CreatePanel();
            var grid = panel.SkillsGrid;
            return (
                grid.SelectionUnit,
                grid.SelectionMode,
                grid.BorderThickness,
                grid.GridLinesVisibility,
                grid.CanUserAddRows,
                grid.CanUserDeleteRows,
                grid.IsReadOnly,
                grid.Columns.Count);
        });

        Assert.Equal(DataGridSelectionUnit.FullRow, details.Item1);
        Assert.Equal(DataGridSelectionMode.Single, details.Item2);
        Assert.Equal(new System.Windows.Thickness(1), details.Item3);
        Assert.Equal(DataGridGridLinesVisibility.None, details.Item4);
        Assert.False(details.Item5);
        Assert.False(details.Item6);
        Assert.True(details.Item7);
        Assert.Equal(4, details.Item8);
    }

    [Fact]
    public void SkillsGrid_SelectionIsRowBasedAndRowsExposeAllColumns()
    {
        var details = RunOnSta(() =>
        {
            var panel = CreatePanel();
            var grid = panel.SkillsGrid;
            grid.SelectedIndex = 0;
            var row = Assert.IsType<SkillSettingsRow>(grid.SelectedItem);
            return (row.Alias, panel.SkillRows.Count, grid.Columns[0].Header, grid.Columns[1].Header, grid.Columns[2].Header, grid.Columns[3].Header);
        });

        Assert.Equal("translate", details.Item1);
        Assert.Equal(1, details.Item2);
        Assert.Equal("Alias", details.Item3);
        Assert.Equal("Path", details.Item4);
        Assert.Equal("Global", details.Item5);
        Assert.Equal("Status", details.Item6);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private NativeSkillSettingsPanel CreatePanel()
    {
        Directory.CreateDirectory(_root);
        var skillPath = Path.Combine(_root, "translate.md");
        File.WriteAllText(skillPath, "translate");
        var repository = new SkillRepository(_root, Path.Combine(_root, "settings"));
        repository.Add("translate", skillPath, global: false);
        var accessor = new TestSettingsAccessor();
        return new NativeSkillSettingsPanel(new SkillSettingsController(repository), accessor);
    }

    private static T RunOnSta<T>(Func<T> callback)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = callback();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
        return result!;
    }

    private sealed class TestSettingsAccessor : IPluginSettingsAccessor
    {
        public AIAskPluginSettings Current { get; } = new();
        public void Save() { }
    }
}
