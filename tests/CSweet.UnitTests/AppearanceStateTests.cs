using CSweet.UI.Services;
using Microsoft.JSInterop;

namespace CSweet.UnitTests;

public sealed class AppearanceStateTests
{
    [Fact]
    public async Task PreferenceSurvivesNewSessionAndIgnoresInvalidValues()
    {
        var storage = new Storage();
        var state = new AppearanceState(storage);
        var changes = 0;
        state.Changed += () => changes++;
        await state.SetThemeAsync("gold");
        Assert.True(changes > 0);
        var next = new AppearanceState(storage);
        await next.InitializeAsync();
        Assert.Equal("gold", next.Theme);
        await next.SetThemeAsync("invalid");
        Assert.Equal("gold", next.Theme);
        storage.Value = "invalid";
        var fallback = new AppearanceState(storage);
        await fallback.InitializeAsync();
        Assert.Equal("aurora", fallback.Theme);
    }

    [Fact]
    public async Task StorageFailureStillAppliesThemeForTheSession()
    {
        var state = new AppearanceState(new Storage { Fail = true });
        await state.InitializeAsync();
        await state.SetThemeAsync("gold");
        Assert.Equal("gold", state.Theme);
        Assert.True(state.PersistenceUnavailable);
    }

    [Fact]
    public async Task DelayedStorageReadDoesNotOverrideNewSelection()
    {
        var read = new TaskCompletionSource<string?>();
        var storage = new Storage { PendingRead = read.Task };
        var state = new AppearanceState(storage);
        var loading = state.InitializeAsync();
        await state.SetThemeAsync("gold");
        read.SetResult("aurora");
        await loading;
        Assert.Equal("gold", state.Theme);
    }

    private sealed class Storage : IJSRuntime
    {
        public string? Value { get; set; }
        public bool Fail { get; init; }
        public Task<string?>? PendingRead { get; init; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (Fail) throw new JSException("Storage unavailable");
            Assert.Equal("csweet.appearance", args![0]);
            if (identifier == "localStorage.getItem") return (T)(object)(PendingRead is null ? Value! : (await PendingRead)!);
            Assert.Equal("localStorage.setItem", identifier);
            Value = (string)args[1]!;
            return default!;
        }
    }
}
