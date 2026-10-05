using System.Runtime.InteropServices;

namespace Renamer.Core;

// A byte-stream copy must not silently omit named streams or decrypt an EFS file.
// In-place renaming does not write any streams and is unaffected by this check.
public static class WindowsContentSafety
{
    public static void EnsureSimpleStream(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.Encrypted)) throw new UserError("该文件使用 Windows EFS 加密。为避免复制时改变加密状态，本软件只允许原地改名：" + path);
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            // FAT filesystems don't support named streams; ERROR_HANDLE_EOF also represents an empty file.
            if (error is 38 or 87) return;
            throw new UserError("无法完整检查文件数据流，已停止复制或移动：" + path + "（系统错误 " + error + "）");
        }
        try
        {
            do
            {
                if (!string.Equals(data.Name, "::$DATA", StringComparison.OrdinalIgnoreCase))
                    throw new UserError("文件包含额外的 NTFS 数据流。为避免遗漏数据，本软件只允许该文件原地改名：" + path);
            } while (FindNextStreamW(handle, out data));
            var error = Marshal.GetLastWin32Error(); if (error != 38) throw new UserError("检查数据流中断，已停止：" + path + "（系统错误 " + error + "）");
        }
        finally { FindClose(handle); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] static extern IntPtr FindFirstStreamW(string name, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FindClose(IntPtr handle);
}
