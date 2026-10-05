using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScheduleApp.Native;

/// <summary>
/// Thông tin tiến trình / cổng TCP của Windows dùng để kiểm tra cổng điều khiển trình duyệt đúng là của trình duyệt ScheduleApp đã mở:
/// bảng cổng đang lắng nghe kèm PID sở hữu (GetExtendedTcpTable), cây tiến trình cha–con, dòng lệnh của một tiến trình.
/// </summary>
internal static class ProcessNet
{
    /// <summary>Một cổng TCP đang lắng nghe và tiến trình sở hữu nó.</summary>
    public readonly record struct Listener(IPAddress Address, int Port, int Pid);

    private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    /// <summary>Mọi cổng TCP đang lắng nghe (IPv4 và IPv6) kèm PID sở hữu.</summary>
    public static List<Listener> TcpListeners()
    {
        var list = new List<Listener>();
        Read(AF_INET, 24, row => new Listener(new IPAddress(Bytes(row, 4, 4)), PortOf(Marshal.ReadInt32(row, 8)), Marshal.ReadInt32(row, 20)), list);
        // MIB_TCP6ROW_OWNER_PID: địa chỉ (16) · scope (4) · cổng (4) · địa chỉ xa (16) · scope (4) · cổng xa (4) · trạng thái (4) · PID (4).
        Read(AF_INET6, 56, row => new Listener(new IPAddress(Bytes(row, 0, 16)), PortOf(Marshal.ReadInt32(row, 20)), Marshal.ReadInt32(row, 52)), list);
        return list;
    }

    private static void Read(int family, int rowSize, Func<IntPtr, Listener> parse, List<Listener> list)
    {
        int size = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var buffer = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
            try
            {
                uint result = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (result == ERROR_INSUFFICIENT_BUFFER) continue; // bảng thay đổi giữa hai lần gọi → cấp lại bộ nhớ
                if (result != 0) throw new InvalidOperationException($"Không đọc được bảng cổng TCP (mã lỗi {result}).");
                int count = Marshal.ReadInt32(buffer);
                for (int i = 0; i < count; i++) list.Add(parse(buffer + 4 + i * rowSize));
                return;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
        throw new InvalidOperationException("Không đọc được bảng cổng TCP (bảng thay đổi liên tục).");
    }

    private static byte[] Bytes(IntPtr row, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(row + offset, bytes, 0, length);
        return bytes;
    }

    /// <summary>Số cổng lưu theo thứ tự byte mạng ở 16 bit thấp.</summary>
    private static int PortOf(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    // ───────────────────────────── Cây tiến trình ─────────────────────────────

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public int dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref PROCESSENTRY32W entry);

    /// <summary>PID → PID tiến trình cha của mọi tiến trình đang chạy.</summary>
    public static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        using var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid) return map;
        var entry = new PROCESSENTRY32W { dwSize = Marshal.SizeOf<PROCESSENTRY32W>() };
        if (!Process32FirstW(snapshot, ref entry)) return map;
        do map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
        while (Process32NextW(snapshot, ref entry));
        return map;
    }

    // ───────────────────────────── Dòng lệnh ─────────────────────────────

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr info, int length, out int returnLength);

    /// <summary>Dòng lệnh của tiến trình (null = không đọc được, vd tiến trình của người dùng khác hoặc đã thoát).</summary>
    public static string? CommandLine(int pid)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid) return null;
        NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out int length);
        if (length <= 0 || length > 1 << 20) return null;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            uint status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _);
            if (status != 0) return null;
            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer } — chuỗi nằm ngay trong vùng nhớ trả về.
            int bytes = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, bytes / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
