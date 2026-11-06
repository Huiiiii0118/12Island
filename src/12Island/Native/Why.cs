using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Why a reading is missing, worded in the interface language when the reading is taken.
internal static class Why
{
    public static string Waiting => Loc.T("等待第二次读数", "waiting for a second reading");
    public static string NoCounters => Loc.T("系统性能计数器不可用", "the system's performance counters are unavailable");
    public static string NoFrequency => Loc.T("处理器未报告频率", "the processor reports no frequency");
    public static string NoMemory => Loc.T("无法读取内存状态", "the memory status could not be read");
    public static string NoNetwork => Loc.T("无法读取网卡", "the network adapters could not be read");
    public static string NoAdapter => Loc.T("未找到物理网卡", "no physical network adapter was found");
    public static string NoGpu => Loc.T("显卡驱动未提供 GPU 计数器", "the graphics driver provides no GPU counters");
    public static string NoGpuMemory => Loc.T("显卡驱动未报告显存占用", "the graphics driver reports no memory in use");
    public static string NoProcesses => Loc.T("无法读取进程列表", "the process list could not be read");
}
