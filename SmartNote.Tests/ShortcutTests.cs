using SmartNote.Application;
using SmartNote.Core;
using System.Text.Json;
using Xunit;
namespace SmartNote.Tests;

public sealed class ShortcutTests
{
    [Fact] public void ExistingSettingsKeepDefaultShortcut() => Assert.Equal("Ctrl+Alt+M", JsonSerializer.Deserialize<Preferences>("{\"Topmost\":true}")!.QuickShortcut);
    [Theory]
    [InlineData("Ctrl+Alt+M", 3u, 77u)]
    [InlineData("Shift+Ctrl+Q", 6u, 81u)]
    [InlineData("Alt+F24", 1u, 135u)]
    [InlineData("Ctrl+0", 2u, 48u)]
    [InlineData("", 0u, 0u)]
    public void ParsesSupportedKeys(string text, uint modifiers, uint key)
    {
        var shortcut = QuickShortcut.Parse(text);
        Assert.Equal(modifiers, shortcut.Modifiers);
        Assert.Equal(key, shortcut.Key);
    }
    [Theory]
    [InlineData("M")]
    [InlineData("Shift+M")]
    [InlineData("Ctrl+F12")]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Win+M")]
    [InlineData("Ctrl+F01")]
    [InlineData("Ctrl+F25")]
    [InlineData(null)]
    public void RejectsUnsafeOrMalformedShortcut(string? text) => Assert.Throws<InvalidDataException>(() => QuickShortcut.Parse(text));
    private sealed class Registrar : IShortcutRegistrar
    {
        public Dictionary<int, uint> Active { get; } = [];
        public bool Reject
        {
            get; set;
        }
        public bool Register(int id, uint modifiers, uint key)
        {
            if (Reject)
                return false;
            Active.Add(id, key);
            return true;
        }
        public void Unregister(int id) => Active.Remove(id);
    }
    [Fact]
    public async Task ReplacementRetainsOldUntilPersistenceCompletes()
    {
        var registrar = new Registrar();
        using var controller = new ShortcutController(registrar);
        await controller.ChangeAsync("Ctrl+Alt+M", () => Task.CompletedTask);
        var old = controller.ActiveId;
        await controller.ChangeAsync("Ctrl+Shift+Q", () => { Assert.Equal(2, registrar.Active.Count); Assert.Equal(old, controller.ActiveId); return Task.CompletedTask; });
        Assert.Single(registrar.Active);
        Assert.NotEqual(old, controller.ActiveId);
        Assert.Equal("Ctrl+Shift+Q", controller.Current);
    }
    [Fact]
    public async Task RegistrationConflictDoesNotSaveOrUnregisterOld()
    {
        var registrar = new Registrar();
        using var controller = new ShortcutController(registrar);
        await controller.ChangeAsync("Ctrl+Alt+M", () => Task.CompletedTask);
        var old = controller.ActiveId;
        registrar.Reject = true;
        var persisted = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => controller.ChangeAsync("Ctrl+Alt+Q", () => { persisted = true; return Task.CompletedTask; }));
        Assert.False(persisted);
        Assert.Equal(old, controller.ActiveId);
        Assert.Single(registrar.Active);
        Assert.Equal("Ctrl+Alt+M", controller.Current);
    }
    [Fact]
    public async Task SaveFailureRollsBackOnlyNewRegistration()
    {
        var registrar = new Registrar();
        using var controller = new ShortcutController(registrar);
        await controller.ChangeAsync("Ctrl+Alt+M", () => Task.CompletedTask);
        var old = controller.ActiveId;
        await Assert.ThrowsAsync<IOException>(() => controller.ChangeAsync("Ctrl+Alt+Q", () => throw new IOException("injected")));
        Assert.Equal(old, controller.ActiveId);
        Assert.Single(registrar.Active);
        Assert.Equal("Ctrl+Alt+M", controller.Current);
    }
    [Fact]
    public async Task DisableIsTransactionalAndSameKeyDoesNotReregister()
    {
        var registrar = new Registrar();
        using var controller = new ShortcutController(registrar);
        await controller.ChangeAsync("Ctrl+Alt+M", () => Task.CompletedTask);
        registrar.Reject = true;
        await controller.ChangeAsync("Alt+Ctrl+M", () => Task.CompletedTask);
        Assert.Single(registrar.Active);
        await Assert.ThrowsAsync<IOException>(() => controller.ChangeAsync("", () => throw new IOException()));
        Assert.Single(registrar.Active);
        await controller.ChangeAsync("", () => Task.CompletedTask);
        Assert.Empty(registrar.Active);
        Assert.Null(controller.ActiveId);
    }
}
