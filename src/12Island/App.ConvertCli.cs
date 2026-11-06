using System.IO;
using TwelveIsland.Core;

namespace TwelveIsland;

// "--convert-to .pdf a.docx b.csv" converts the files and writes what happened to convert.log in the data folder, then quits.
// It is the conversion page's work without the page, for a script or for checking a build.
public partial class App
{
    private void RunConvertTo(string[] arguments)
    {
        var list = arguments.ToList();
        int at = list.FindIndex(argument => string.Equals(argument, "--convert-to", StringComparison.OrdinalIgnoreCase));
        string extension = at >= 0 && at + 1 < list.Count ? list[at + 1] : ".pdf";
        if (!extension.StartsWith('.')) extension = "." + extension;
        var files = list.Skip(at + 2).Where(File.Exists).ToList();
        if (files.Count == 0) { Shutdown(); return; }
        _ = ConvertAndQuitAsync(extension, files);
    }

    private async Task ConvertAndQuitAsync(string extension, List<string> files)
    {
        var log = new List<string>();
        var converter = new FileConverter(Settings);
        foreach (var file in files)
        {
            try
            {
                var target = ConvertFormats.TargetsFor(file, converter.HasFfmpeg, converter.HasOffice)
                    .FirstOrDefault(item => item.Extension == extension);
                if (target is null) { log.Add($"{file} | unsupported | {extension}"); continue; }
                string output = await Task.Run(() => converter.ConvertAsync(file, target, Settings.ConvertOutputFolder, null, CancellationToken.None));
                log.Add($"{file} | ok | {output} | {new FileInfo(output).Length} bytes");
            }
            catch (Exception ex) { log.Add($"{file} | failed | {ex.GetType().Name}: {ex.Message}"); }
        }
        try
        {
            File.WriteAllLines(Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "12Island"), "convert.log"), log);
        }
        catch (IOException) { }
        Shutdown();
    }
}
