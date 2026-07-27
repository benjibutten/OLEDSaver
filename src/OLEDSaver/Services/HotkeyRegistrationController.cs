using System.Windows;
using OLEDSaver.Helpers;
using OLEDSaver.Input;

namespace OLEDSaver.Services;

public interface IHotkeyRegistrationService
{
    bool Register(object host, int id, HotkeyDefinition hotkey);

    void Unregister(object host, int id);
}

/// <summary>
/// Owns registration of the one global hotkey. The interface seam keeps the
/// re-registration order (always unregister first, or user32 rejects the second
/// registration for the same id) testable without a window.
/// </summary>
public sealed class HotkeyRegistrationController
{
    private readonly IHotkeyRegistrationService _service;

    public HotkeyRegistrationController(IHotkeyRegistrationService? service = null)
    {
        _service = service ?? new NativeHotkeyRegistrationService();
    }

    public bool Register(object host, int id, HotkeyDefinition hotkey) => _service.Register(host, id, hotkey);

    public void Unregister(object host, int id) => _service.Unregister(host, id);

    public bool ReRegister(object host, int id, HotkeyDefinition hotkey)
    {
        _service.Unregister(host, id);
        return _service.Register(host, id, hotkey);
    }

    private sealed class NativeHotkeyRegistrationService : IHotkeyRegistrationService
    {
        public bool Register(object host, int id, HotkeyDefinition hotkey)
        {
            return hotkey.IsSet
                && host is Window window
                && NativeInterop.RegisterGlobalHotkey(window, id, (uint)hotkey.Modifiers, hotkey.VirtualKey);
        }

        public void Unregister(object host, int id)
        {
            if (host is Window window)
                NativeInterop.UnregisterGlobalHotkey(window, id);
        }
    }
}
