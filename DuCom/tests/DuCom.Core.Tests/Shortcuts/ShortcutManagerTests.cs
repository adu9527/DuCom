namespace DuCom.Core.Tests.Shortcuts;

using System.Text.Json;
using DuCom.Services.Shortcuts;

public sealed class ShortcutManagerTests : IDisposable
{
    private readonly string _tempDirectory;

    public ShortcutManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"DuComShortcutsTests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void RegisterDefaultActions_AreUnique()
    {
        ShortcutManager manager = CreateManagerWithDefaults();

        string[] ids = manager.Definitions.Select(definition => definition.ActionId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void RegisterDefaultActions_HaveValidDefaultGestures()
    {
        ShortcutManager manager = CreateManagerWithDefaults();

        Assert.All(manager.Definitions, definition =>
        {
            if (definition.DefaultGesture is null)
            {
                // Unbound-by-default actions are allowed; they must never conflict.
                Assert.False(definition.HasConflict);
                return;
            }

            Assert.False(definition.DefaultGesture.IsModifierOnly);
        });
    }

    [Fact]
    public void RegisterDefaultActions_FormatJsonAndJoinLines_AreUnboundByDefault()
    {
        ShortcutManager manager = CreateManagerWithDefaults();

        foreach (string actionId in (string[])["FormatJson", "JoinLines"])
        {
            ShortcutDefinition? definition = manager.GetDefinition(actionId);
            Assert.NotNull(definition);
            Assert.Null(definition!.DefaultGesture);
        }
    }

    [Fact]
    public void SetGesture_ModifierOnly_ReturnsInvalid()
    {
        ShortcutManager manager = CreateManagerWithDefaults();

        ShortcutKeyGesture? gesture = ShortcutKeyGesture.Parse("Ctrl+Shift");
        Assert.NotNull(gesture);
        ShortcutConflictResult result = manager.SetGesture("ClearDisplay", gesture);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void SetGesture_ConflictingGesture_ReturnsInvalid()
    {
        ShortcutManager manager = CreateManagerWithDefaults();

        ShortcutKeyGesture? gesture = ShortcutKeyGesture.Parse("F5");
        Assert.NotNull(gesture);
        ShortcutConflictResult result = manager.SetGesture("ClearDisplay", gesture);

        Assert.False(result.IsValid);
        Assert.Contains("RefreshPorts", result.ConflictingActionIds);
    }

    [Fact]
    public void SetGesture_ValidGesture_UpdatesDefinition()
    {
        ShortcutManager manager = CreateManagerWithDefaults();

        ShortcutKeyGesture? gesture = ShortcutKeyGesture.Parse("Ctrl+Shift+K");
        Assert.NotNull(gesture);
        ShortcutConflictResult result = manager.SetGesture("ClearDisplay", gesture);

        Assert.True(result.IsValid);
        Assert.Equal("Ctrl+Shift+K", manager.GetDefinition("ClearDisplay")!.GestureText);
    }

    [Fact]
    public void SaveAndLoad_RoundTripPreservesGestures()
    {
        string path = Path.Combine(_tempDirectory, "shortcuts.json");
        ShortcutManager first = CreateManagerWithDefaults();
        first.SetGesture("ClearDisplay", ShortcutKeyGesture.Parse("Ctrl+Shift+K")!);
        first.Save(path);

        ShortcutManager second = CreateManagerWithDefaults();
        bool loaded = second.TryLoad(path);

        Assert.True(loaded);
        Assert.Equal("Ctrl+Shift+K", second.GetDefinition("ClearDisplay")!.GestureText);
    }

    [Fact]
    public void SaveAndLoad_RoundTripPreservesEnabledState()
    {
        string path = Path.Combine(_tempDirectory, "shortcuts-enabled.json");
        ShortcutManager first = CreateManagerWithDefaults();
        first.SetEnabled("ClearDisplay", false);
        first.Save(path);

        ShortcutManager second = CreateManagerWithDefaults();
        Assert.True(second.TryLoad(path));

        Assert.False(second.GetDefinition("ClearDisplay")!.IsEnabled);
        Assert.Null(second.FindActionId(ShortcutKeyGesture.Parse("Ctrl+L")!));
    }

    [Fact]
    public void DisabledShortcutDoesNotConflictWithEnabledShortcut()
    {
        ShortcutManager manager = CreateManagerWithDefaults();
        manager.SetEnabled("RefreshPorts", false);

        ShortcutConflictResult result = manager.SetGesture("ClearDisplay", ShortcutKeyGesture.Parse("F5")!);

        Assert.True(result.IsValid);
        Assert.False(manager.GetDefinition("ClearDisplay")!.HasConflict);
    }

    [Fact]
    public void LegacyConfigurationWithoutEnabledStateDefaultsToEnabled()
    {
        string path = Path.Combine(_tempDirectory, "legacy-shortcuts.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "shortcuts": [
                { "actionId": "ClearDisplay", "gesture": "Ctrl+L" }
              ]
            }
            """);
        ShortcutManager manager = CreateManagerWithDefaults();

        Assert.True(manager.TryLoad(path));
        Assert.True(manager.GetDefinition("ClearDisplay")!.IsEnabled);
    }

    [Fact]
    public void TryLoad_InvalidJson_FallsBackToDefaults()
    {
        string path = Path.Combine(_tempDirectory, "shortcuts.json");
        File.WriteAllText(path, "{ not valid json");

        ShortcutManager manager = CreateManagerWithDefaults();
        manager.SetGesture("ClearDisplay", ShortcutKeyGesture.Parse("Ctrl+Shift+K")!);
        bool loaded = manager.TryLoad(path);

        Assert.False(loaded);
        Assert.Equal("Ctrl+L", manager.GetDefinition("ClearDisplay")!.GestureText);
    }

    [Fact]
    public void TryLoad_MissingFile_FallsBackToDefaults()
    {
        string path = Path.Combine(_tempDirectory, "missing.json");
        ShortcutManager manager = CreateManagerWithDefaults();
        bool loaded = manager.TryLoad(path);

        Assert.False(loaded);
        Assert.NotEmpty(manager.Definitions);
    }

    [Fact]
    public void ResetToDefault_RestoresSingleAction()
    {
        ShortcutManager manager = CreateManagerWithDefaults();
        manager.SetGesture("ClearDisplay", ShortcutKeyGesture.Parse("Ctrl+Shift+K")!);

        manager.ResetToDefault("ClearDisplay");

        Assert.Equal("Ctrl+L", manager.GetDefinition("ClearDisplay")!.GestureText);
    }

    [Fact]
    public void ResetAllToDefaults_RestoresAllActions()
    {
        ShortcutManager manager = CreateManagerWithDefaults();
        manager.SetGesture("ClearDisplay", ShortcutKeyGesture.Parse("Ctrl+Shift+K")!);
        manager.SetGesture("RefreshPorts", ShortcutKeyGesture.Parse("Ctrl+Shift+R")!);

        manager.ResetAllToDefaults();

        Assert.Equal("Ctrl+L", manager.GetDefinition("ClearDisplay")!.GestureText);
        Assert.Equal("F5", manager.GetDefinition("RefreshPorts")!.GestureText);
    }

    [Fact]
    public void FindActionId_ReturnsMatchingAction()
    {
        ShortcutManager manager = CreateManagerWithDefaults();

        string? actionId = manager.FindActionId(ShortcutKeyGesture.Parse("Ctrl+L")!);

        Assert.Equal("ClearDisplay", actionId);
    }

    [Fact]
    public void FindActionId_WithConflict_ReturnsNull()
    {
        string path = Path.Combine(_tempDirectory, "conflict.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "shortcuts": [
                { "actionId": "ClearDisplay", "gesture": "Ctrl+L" },
                { "actionId": "RefreshPorts", "gesture": "Ctrl+L" }
              ]
            }
            """);

        ShortcutManager manager = CreateManagerWithDefaults();
        bool loaded = manager.TryLoad(path);

        Assert.True(loaded);
        Assert.All(manager.Definitions.Where(definition => definition.ActionId is "ClearDisplay" or "RefreshPorts"), definition =>
        {
            Assert.True(definition.HasConflict, $"{definition.ActionId} should have conflict");
            Assert.Equal("Ctrl+L", definition.GestureText);
        });

        string? actionId = manager.FindActionId(ShortcutKeyGesture.Parse("Ctrl+L")!);

        Assert.Null(actionId);
    }

    [Fact]
    public void SetGesture_DuplicateRegistration_Throws()
    {
        ShortcutManager manager = new();
        manager.Register(new ShortcutAction("Test", "Test", "F1", "Test"));

        Assert.Throws<InvalidOperationException>(() =>
            manager.Register(new ShortcutAction("Test", "Test", "F2", "Test")));
    }

    private static ShortcutManager CreateManagerWithDefaults()
    {
        var manager = new ShortcutManager();
        manager.RegisterDefaultActions();
        return manager;
    }
}
