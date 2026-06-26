using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Opens the Windows Start menu. There is no public "open Start" API, so the
/// reliable, well-established approach is to synthesize a left-Windows keystroke
/// via SendInput.
/// </summary>
public static class StartMenu
{
    public static void Open()
    {
        Span<INPUT> inputs =
        [
            KeyEvent(VIRTUAL_KEY.VK_LWIN, keyUp: false),
            KeyEvent(VIRTUAL_KEY.VK_LWIN, keyUp: true),
        ];

        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyEvent(VIRTUAL_KEY key, bool keyUp)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki = new KEYBDINPUT
        {
            wVk = key,
            dwFlags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
        };
        return input;
    }
}
