using System.Text;
using System.Text.RegularExpressions;

namespace VideoConverter.Core;

public static class InputFiles
{
    private static readonly HashSet<string> videoTypes = [".mp4", ".mov", ".mkv", ".m4v", ".webm", ".avi", ".mts", ".m2ts", ".ts", ".mxf", ".mpg", ".mpeg", ".wmv"];
    private static string ExpandHome(string path) => path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : path.StartsWith("~/") || path.StartsWith(@"~\") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;
    public static (string Path, string Relative)[] Expand(CliArguments args)
    {
        var found = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string relative) { path = Path.GetFullPath(path); found.TryAdd(path, (path, relative)); }
        foreach (var raw in args.Inputs)
        {
            var path = ExpandHome(raw);
            if (Directory.Exists(path))
            {
                foreach (var p in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = args.Has("recursive"), AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Order(StringComparer.OrdinalIgnoreCase))
                    if (videoTypes.Contains(Path.GetExtension(p).ToLowerInvariant())) Add(p, Path.GetRelativePath(path, p));
            }
            else if (path.IndexOfAny(['*', '?', '[']) >= 0)
            {
                if (path.Contains("**") && !args.Has("recursive")) throw new CliException("invalid_arguments", "** 通配符需要 --recursive。");
                var absolute = Path.GetFullPath(path).Replace('\\', '/');
                var first = absolute.IndexOfAny(['*', '?', '[']);
                var slash = absolute.LastIndexOf('/', first);
                var root = absolute[..(slash + 1)];
                var pattern = GlobPattern(absolute);
                var files = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", new EnumerationOptions
                    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                    .Select(Path.GetFullPath).Where(p => pattern.IsMatch(p.Replace('\\', '/'))).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
                var common = files.Length > 0 ? Path.GetDirectoryName(files[0])! : Directory.GetCurrentDirectory();
                foreach (var file in files)
                    while (Path.GetRelativePath(common, Path.GetDirectoryName(file)!).Split(Path.DirectorySeparatorChar)[0] == "..")
                        common = Path.GetDirectoryName(common)!;
                foreach (var file in files) Add(file, Path.GetRelativePath(common, file));
            }
            else if (File.Exists(path) || args.Has("continue_on_error")) Add(path, Path.GetFileName(path));
            else throw new CliException("input_unavailable", $"输入不存在：{raw}", 4);
        }
        if (found.Count == 0) throw new CliException("input_unavailable", "没有找到匹配的视频。", 4);
        if (args.Get("output") is not null && (found.Count != 1 || args.Get("output_dir") is not null))
            throw new CliException("invalid_arguments", "--output 需要单个输入，且不能与 --output-dir 同时使用。");
        return found.Values.ToArray();
    }
    private static Regex GlobPattern(string glob)
    {
        var pattern = new StringBuilder(@"\A");
        for (int i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { pattern.Append("(?:.*/)?"); i++; }
                else pattern.Append(".*");
            }
            else if (glob[i] == '*') pattern.Append("[^/]*");
            else if (glob[i] == '?') pattern.Append("[^/]");
            else if (glob[i] == '[' && glob.IndexOf(']', i + 1) is var end && end > i + 1)
            {
                var content = glob[(i + 1)..end];
                pattern.Append('[').Append(content.StartsWith('!') ? "^" + content[1..] : content).Append(']'); i = end;
            }
            else pattern.Append(Regex.Escape(glob[i].ToString()));
        }
        return new Regex(pattern.Append(@"\z").ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
