using System.Windows.Input;
using OLEDSaver.Input;
using Xunit;

namespace OLEDSaver.Tests;

public class HotkeyDefinitionTests
{
    private const uint VkB = 0x42;
    private const uint VkF12 = 0x7B;
    private const uint VkLeftControl = 0xA2;

    [Fact]
    public void Default_is_control_alt_b()
    {
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyDefinition.Default.Modifiers);
        Assert.Equal(VkB, HotkeyDefinition.Default.VirtualKey);
        Assert.Equal("Ctrl + Alt + B", HotkeyDefinition.Default.DisplayText);
    }

    [Fact]
    public void Combination_describes_every_modifier_in_a_stable_order()
    {
        var hotkey = new HotkeyDefinition(
            HotkeyModifiers.Windows | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Control,
            VkF12);

        Assert.Equal("Ctrl + Alt + Shift + Win + F12", hotkey.DisplayText);
    }

    [Theory]
    [InlineData(0x10)] // VK_SHIFT
    [InlineData(0x11)] // VK_CONTROL
    [InlineData(0x12)] // VK_MENU
    [InlineData(0x5B)] // VK_LWIN
    [InlineData(0xA2)] // VK_LCONTROL
    [InlineData(0xA5)] // VK_RMENU
    public void A_modifier_alone_is_not_a_hotkey(uint virtualKey)
    {
        // RegisterHotKey cannot express "Ctrl" as the key of a combination, so
        // recording one has to fall back to "not set" rather than half-register.
        var hotkey = new HotkeyDefinition(HotkeyModifiers.Control, virtualKey);

        Assert.False(hotkey.IsSet);
        Assert.Equal(HotkeyModifiers.None, hotkey.Modifiers);
        Assert.Equal("Not set", hotkey.DisplayText);
    }

    [Fact]
    public void Out_of_range_virtual_keys_are_rejected()
    {
        Assert.False(new HotkeyDefinition(HotkeyModifiers.Control, 0x1FF).IsSet);
        Assert.False(new HotkeyDefinition(HotkeyModifiers.Control, 0).IsSet);
    }

    [Fact]
    public void Bare_keys_are_allowed_but_flagged()
    {
        var bare = new HotkeyDefinition(HotkeyModifiers.None, VkF12);

        Assert.True(bare.IsSet);
        Assert.True(bare.HasNoModifier);
        Assert.False(HotkeyDefinition.Default.HasNoModifier);
    }

    [Fact]
    public void Wpf_modifiers_map_onto_the_RegisterHotKey_flags()
    {
        HotkeyModifiers modifiers = HotkeyDefinition.FromWpfModifiers(
            ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);

        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows,
            modifiers);

        // The values themselves are user32's, since they are passed straight through.
        Assert.Equal(0x0002u, (uint)HotkeyModifiers.Control);
        Assert.Equal(0x0001u, (uint)HotkeyModifiers.Alt);
        Assert.Equal(0x0004u, (uint)HotkeyModifiers.Shift);
        Assert.Equal(0x0008u, (uint)HotkeyModifiers.Windows);
    }

    [Fact]
    public void FromKey_keeps_the_modifiers_held_with_the_key()
    {
        var hotkey = HotkeyDefinition.FromKey(Key.B, ModifierKeys.Control | ModifierKeys.Alt);

        Assert.Equal(HotkeyDefinition.Default, hotkey);
    }

    [Theory]
    [InlineData(0x20, "Space")]
    [InlineData(0x1B, "Escape")]
    [InlineData(0x7B, "F12")]
    [InlineData(0x62, "Numpad 2")]
    [InlineData(0x26, "Up")]
    [InlineData(0x42, "B")]
    [InlineData(0x31, "1")]
    public void Keys_get_readable_names(uint virtualKey, string expected)
    {
        Assert.Equal(expected, HotkeyDefinition.DescribeVirtualKey(virtualKey));
    }

    [Fact]
    public void Equality_covers_key_and_modifiers()
    {
        Assert.Equal(new HotkeyDefinition(HotkeyModifiers.Control, VkB), new HotkeyDefinition(HotkeyModifiers.Control, VkB));
        Assert.NotEqual(new HotkeyDefinition(HotkeyModifiers.Control, VkB), new HotkeyDefinition(HotkeyModifiers.Alt, VkB));
        Assert.NotEqual(new HotkeyDefinition(HotkeyModifiers.Control, VkB), new HotkeyDefinition(HotkeyModifiers.Control, VkF12));
    }

    [Fact]
    public void Modifier_keys_are_recognized()
    {
        Assert.True(HotkeyDefinition.IsModifierKey(VkLeftControl));
        Assert.False(HotkeyDefinition.IsModifierKey(VkB));
    }
}
