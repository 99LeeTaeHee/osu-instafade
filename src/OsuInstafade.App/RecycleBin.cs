using System.IO;
using System.Runtime.InteropServices;

namespace OsuInstafade.App;

/// <summary>파일이나 폴더를 휴지통으로 옮긴다 (완전히 지우지 않음 → 휴지통에서 되살릴 수 있음).</summary>
internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000; // 휴지통에 못 넣으면(네트워크 드라이브 등) 완전 삭제 전에 경고

    /// <summary>성공하면 true. 사용자가 취소했거나 실패하면 false.</summary>
    public static bool Send(string path)
    {
        path = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int r = SHFileOperation(ref op);
        return r == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }
}
