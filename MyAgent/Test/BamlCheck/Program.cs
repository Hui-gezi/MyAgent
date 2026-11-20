// 列出 WPF 程序集内嵌的 BAML 页面，验证 ArchiveWindow 等新窗口已编入。
// 用法: dotnet run --project Test/BamlCheck -- <MyAgent.dll 路径>
using System.IO;
using System.Reflection;
using System.Resources;

var dll = Path.GetFullPath(args.Length > 0 ? args[0]
    : "src/MyAgent.Desktop/bin/Release/net9.0-windows/MyAgent.dll");
var asm = Assembly.LoadFile(dll);
foreach (var name in asm.GetManifestResourceNames())
{
    Console.WriteLine("resource: " + name);
    if (!name.EndsWith(".g.resources")) continue;
    using var stream = asm.GetManifestResourceStream(name)!;
    using var reader = new ResourceReader(stream);
    foreach (System.Collections.DictionaryEntry entry in reader)
        Console.WriteLine("  " + entry.Key);
}
Console.WriteLine("ArchiveWindow type present: " + (asm.GetType("MyAgent.Desktop.ArchiveWindow") is not null));
