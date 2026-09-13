using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace GitKay.UI;

internal static class MainWindowNavigation
{
    public static bool TryGetListNavigationDelta(Key key, KeyModifiers modifiers, out int delta)
    {
        delta = 0;

        if (modifiers != KeyModifiers.None)
        {
            return false;
        }

        switch (key)
        {
            case Key.J:
            case Key.Down:
                delta = 1;
                return true;
            case Key.K:
            case Key.Up:
                delta = -1;
                return true;
            default:
                return false;
        }
    }

    public static bool TryMoveSelection(ListBox listBox, int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        var itemCount = listBox.Items.Count;
        if (itemCount <= 0)
        {
            return false;
        }

        var selectedIndex = listBox.SelectedIndex;
        var nextIndex = GetNextIndex(selectedIndex, itemCount, delta);

        if (nextIndex < 0 || nextIndex == selectedIndex)
        {
            return false;
        }

        listBox.SelectedIndex = nextIndex;

        if (listBox.Items[nextIndex] is { } item)
        {
            listBox.ScrollIntoView(item);
        }

        return true;
    }

    internal static int GetNextIndex(int selectedIndex, int itemCount, int delta)
    {
        if (itemCount <= 0)
        {
            return -1;
        }

        if (selectedIndex < 0)
        {
            return delta > 0 ? 0 : itemCount - 1;
        }

        return Math.Clamp(selectedIndex + delta, 0, itemCount - 1);
    }
}
