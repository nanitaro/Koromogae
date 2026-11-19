namespace VstCore;

/// <summary>エディタウィンドウのメニューバーに付ける「プリセット」メニュー。</summary>
internal static class ProgramMenu
{
    public const int BaseId = 0x3000;
    const int GroupSize = 32;

    /// <summary>プリセットが 2 つ未満なら null。多い場合は 32 件ずつのサブメニューに分ける。</summary>
    public static IntPtr Build(string[] names)
    {
        if (names.Length < 2) return IntPtr.Zero;

        IntPtr bar = Win32.CreateMenu();
        IntPtr root = Win32.CreatePopupMenu();

        if (names.Length <= GroupSize * 2)
        {
            for (int i = 0; i < names.Length; i++) AddItem(root, i, names[i]);
        }
        else
        {
            for (int start = 0; start < names.Length; start += GroupSize)
            {
                int end = Math.Min(start + GroupSize, names.Length);
                IntPtr group = Win32.CreatePopupMenu();
                for (int i = start; i < end; i++) AddItem(group, i, names[i]);
                Win32.AppendMenuW(root, Win32.MF_POPUP, (UIntPtr)(nuint)group, $"{start + 1} - {end}");
            }
        }

        Win32.AppendMenuW(bar, Win32.MF_POPUP, (UIntPtr)(nuint)root, CoreLoc.T("Core.Presets"));
        return bar;
    }

    static void AddItem(IntPtr menu, int index, string name)
        => Win32.AppendMenuW(menu, Win32.MF_STRING, (UIntPtr)(uint)(BaseId + index), $"{index + 1}. {name.Replace("&", "&&")}");

    public static void Check(IntPtr bar, int count, int current)
    {
        if (bar == IntPtr.Zero) return;
        for (int i = 0; i < count; i++)
            Win32.CheckMenuItem(bar, (uint)(BaseId + i), Win32.MF_BYCOMMAND | (i == current ? Win32.MF_CHECKED : Win32.MF_UNCHECKED));
    }

    /// <summary>WM_COMMAND の wParam からプリセット番号を取り出す。該当しなければ -1。</summary>
    public static int IndexFromCommand(IntPtr wParam, int count)
    {
        int id = (int)((ulong)wParam & 0xFFFF);
        int index = id - BaseId;
        return index >= 0 && index < count ? index : -1;
    }
}
