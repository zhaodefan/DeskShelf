using DeskShelf.Application.Catalog;
using DeskShelf.Application.Services;
using DeskShelf.Application.Settings;
using DeskShelf.Application.Undo;
using DeskShelf.Domain.Shelf;
using DeskShelf.Domain.Units;
using DeskShelf.Domain.ValueObjects;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>领域层：集合语义、能力声明与类型注册表。</summary>
public sealed class DomainTests
{
    [Fact]
    public void UnitCatalog_RegistersEveryKind()
    {
        // 新增 unit 类型却忘记登记时，这条测试会立刻失败
        Assert.Empty(UnitCatalog.FindUnregisteredKinds());
    }

    [Fact]
    public void UnitCatalog_ViewKeysAreUnique()
    {
        var viewKeys = Enum.GetValues<UnitKind>()
            .Select(kind => UnitCatalog.Get(kind).ViewKey)
            .ToList();

        Assert.Equal(viewKeys.Count, viewKeys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Capabilities_MatchAgreedBehaviourMatrix()
    {
        var text = UnitCatalog.Get(UnitKind.Text);
        Assert.True(text.Capabilities.CanCopyToClipboard);
        Assert.True(text.Capabilities.CanEditText);
        Assert.Equal(UnitOpenBehavior.None, text.Capabilities.OpenBehavior);

        var image = UnitCatalog.Get(UnitKind.Image);
        Assert.True(image.Capabilities.CanCopyToClipboard);
        Assert.False(image.Capabilities.CanEditText);
        Assert.Equal(UnitOpenBehavior.OpenImage, image.Capabilities.OpenBehavior);

        var folder = UnitCatalog.Get(UnitKind.Folder);
        Assert.False(folder.Capabilities.CanCopyToClipboard);
        Assert.False(folder.Capabilities.CanEditText);
        Assert.Equal(UnitOpenBehavior.OpenFolder, folder.Capabilities.OpenBehavior);

        // 文件夹 unit 只能由拖拽产生
        Assert.False(folder.SupportsPaste);
        Assert.True(folder.SupportsDrop);
    }

    [Fact]
    public void TextUnit_Normalize_UnifiesNewlinesAndTrims()
    {
        Assert.Equal("a\nb", TextUnit.Normalize("  a\r\nb  "));
        Assert.Equal(string.Empty, TextUnit.Normalize(null));
        Assert.Equal(TextUnit.MaxLength, TextUnit.Normalize(new string('x', TextUnit.MaxLength + 500)).Length);
    }

    [Fact]
    public void FolderUnit_Normalize_KeepsDriveRootSeparator()
    {
        var root = FolderUnit.Normalize(@"C:\");
        Assert.Equal(@"C:\", root);

        var nested = FolderUnit.Normalize(Path.GetTempPath() + Path.DirectorySeparatorChar);
        Assert.False(nested.EndsWith(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Shelf_RemoveMany_SkipsUnknownIds()
    {
        var shelf = new Shelf();
        var unit = MakeText("A");
        shelf.Append(unit);

        var removed = shelf.RemoveMany(new[] { unit.Id, UnitId.New() });

        Assert.Single(removed);
        Assert.Empty(shelf.Units);
    }

    [Fact]
    public void Shelf_Move_ClampsTargetIndex()
    {
        var shelf = new Shelf();
        var a = MakeText("A");
        var b = MakeText("B");
        shelf.Append(a);
        shelf.Append(b);

        Assert.True(shelf.Move(a.Id, 99));
        Assert.Equal(new[] { "B", "A" }, shelf.Units.Cast<TextUnit>().Select(u => u.Text));
    }

    [Fact]
    public void Shelf_Insert_ClampsNegativeIndex()
    {
        var shelf = new Shelf();
        shelf.Append(MakeText("A"));
        shelf.Insert(-5, MakeText("Z"));

        Assert.Equal("Z", ((TextUnit)shelf.Units[0]).Text);
    }

    [Fact]
    public void UndoStack_DropsOldestBeyondCapacity()
    {
        var stack = new UndoStack(capacity: 2);
        var applied = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            stack.Record(new RecordingAction($"a{i}", applied));
        }

        Assert.Equal(2, stack.Count);
        Assert.Equal("a2", stack.Undo()?.Description);
        Assert.Equal("a1", stack.Undo()?.Description);
        Assert.Null(stack.Undo());
    }

    private static TextUnit MakeText(string text)
        => new(text, UnitId.New(), UnitOrigin.Clipboard, DateTimeOffset.UtcNow);

    private sealed class RecordingAction : IUndoableAction
    {
        private readonly List<string> _log;

        public RecordingAction(string description, List<string> log)
        {
            Description = description;
            _log = log;
        }

        public string Description { get; }

        public void Apply() => _log.Add($"apply:{Description}");

        public void Revert() => _log.Add($"revert:{Description}");
    }
}

/// <summary>设置校验：非法值必须被收敛，避免下游到处做防御。</summary>
public sealed class SettingsValidatorTests
{
    [Fact]
    public void Normalize_ClampsPanelSize()
    {
        var settings = ShelfSettings.Default with
        {
            Placement = new WindowPlacement { Width = 10, Height = 99999 },
        };

        var normalized = ShelfSettingsValidator.Normalize(settings);

        Assert.Equal(ShelfSettingsValidator.MinPanelWidth, normalized.Placement.Width);
        Assert.Equal(ShelfSettingsValidator.MaxPanelHeight, normalized.Placement.Height);
    }

    [Fact]
    public void Normalize_ReplacesInvalidHotkeyWithDefault()
    {
        var settings = ShelfSettings.Default with
        {
            Hotkey = new HotkeyGesture { Alt = false, Ctrl = false, Key = "X" },
        };

        var normalized = ShelfSettingsValidator.Normalize(settings);

        Assert.Equal(HotkeyGesture.Default.DisplayText, normalized.Hotkey.DisplayText);
        Assert.Equal("Alt+X", normalized.Hotkey.DisplayText);
    }

    [Fact]
    public void Normalize_ClampsUnitLimit()
    {
        var normalized = ShelfSettingsValidator.Normalize(ShelfSettings.Default with { MaxUnits = 1 });
        Assert.Equal(ShelfSettingsValidator.MinUnitLimit, normalized.MaxUnits);
    }

    [Fact]
    public void Normalize_NullReturnsDefault()
    {
        Assert.Equal(ShelfSettings.Default, ShelfSettingsValidator.Normalize(null));
    }

    [Fact]
    public void IsPlacementVisible_DetectsOffscreenWindow()
    {
        var screens = new[] { new ScreenRect(0, 0, 1920, 1040) };

        Assert.True(ShelfSettingsValidator.IsPlacementVisible(
            new WindowPlacement { Left = 1500, Top = 500, Width = 360, Height = 480 }, screens));

        // 副屏被拔除后，记忆位置落在所有显示器之外
        Assert.False(ShelfSettingsValidator.IsPlacementVisible(
            new WindowPlacement { Left = -5000, Top = 500, Width = 360, Height = 480 }, screens));

        // 尚未定位过
        Assert.False(ShelfSettingsValidator.IsPlacementVisible(WindowPlacement.Default, screens));
    }
}
