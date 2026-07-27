using OLEDSaver.Services;
using Xunit;

namespace OLEDSaver.Tests;

public class StartupRegistrySyncServiceTests
{
    private const string ExePath = @"C:\Program Files\OLED Saver\OLEDSaver.exe";

    [Fact]
    public void Enabling_writes_the_startup_command()
    {
        var store = new FakeStore();
        var service = new StartupRegistrySyncService(new FakeFactory(store));

        Assert.True(service.Sync(startWithWindows: true, ExePath));

        Assert.Equal(
            $"\"{ExePath}\" --minimized",
            store.Values[StartupRegistrySyncService.AppRegistryName]);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public void The_startup_command_starts_hidden_in_the_tray()
    {
        // Without --minimized the settings window would open at every logon.
        Assert.Contains("--minimized", StartupRegistrySyncService.BuildStartupCommand(ExePath));

        // The path is quoted, or "Program Files" would split into two arguments.
        Assert.StartsWith($"\"{ExePath}\"", StartupRegistrySyncService.BuildStartupCommand(ExePath));
    }

    [Fact]
    public void Disabling_deletes_the_value_and_tolerates_it_being_absent()
    {
        var store = new FakeStore();
        var service = new StartupRegistrySyncService(new FakeFactory(store));

        Assert.True(service.Sync(startWithWindows: false, ExePath));

        Assert.Equal(new[] { StartupRegistrySyncService.AppRegistryName }, store.DeletedNames);
        Assert.False(store.ThrowOnMissingRequested);
    }

    [Fact]
    public void Nothing_happens_without_an_executable_path()
    {
        var store = new FakeStore();
        var service = new StartupRegistrySyncService(new FakeFactory(store));

        Assert.False(service.Sync(startWithWindows: true, exePath: null));
        Assert.False(service.Sync(startWithWindows: true, exePath: "   "));
        Assert.Empty(store.Values);
    }

    [Fact]
    public void An_unavailable_registry_key_reports_failure_rather_than_throwing()
    {
        var service = new StartupRegistrySyncService(new FakeFactory(null));

        Assert.False(service.Sync(startWithWindows: true, ExePath));
    }

    private sealed class FakeFactory : IStartupRegistryStoreFactory
    {
        private readonly FakeStore? _store;

        public FakeFactory(FakeStore? store)
        {
            _store = store;
        }

        public IStartupRegistryStore? OpenCurrentUserRunKey() => _store;
    }

    private sealed class FakeStore : IStartupRegistryStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public List<string> DeletedNames { get; } = new();

        public bool ThrowOnMissingRequested { get; private set; }

        public bool IsDisposed { get; private set; }

        public void SetValue(string name, string value) => Values[name] = value;

        public void DeleteValue(string name, bool throwOnMissingValue)
        {
            DeletedNames.Add(name);
            ThrowOnMissingRequested |= throwOnMissingValue;
        }

        public void Dispose() => IsDisposed = true;
    }
}
