namespace TwelveIsland.Core;

// What the system monitor page's drawn parts say to a screen reader, where the words depend on whether there is a reading, kept apart
// from the controls that draw them so the wording is checked on its own.
public static class MonitorSpeech
{
    // The large GPU block, named as the block it is so it never reads the same as the small GPU figure beside the main gauge that does
    // the same thing: "GPU 占用块：占用 12% · 显存 18%，切换为以 GPU 为主". usage: 0 to 1, or null while the adapter has no reading,
    // when it says what the tooltip's first line says (note: "暂无读数：…") and never a usage of zero; vram: the memory line, if drawn.
    public static string GpuBlock(double? usage, double? vram, string note, string action)
    {
        string head = Loc.T("GPU 占用块：", "GPU block: ");
        string body = usage is { } used
            ? Loc.T("占用 ", "usage ") + MonitorFormat.Percent(used) + "%" + (vram is { } memory ? Loc.T(" · 显存 ", " · VRAM ") + MonitorFormat.Percent(memory) + "%" : "")
            : note.Length > 0 ? note : Loc.T("暂无读数", "No reading");
        return head + body + Loc.T("，", ", ") + action;
    }
}
