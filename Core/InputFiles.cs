using System.Text;
using System.Text.RegularExpressions;

namespace VideoConverter.Core;

/// <summary>Expands CLI inputs into deterministic, de-duplicated absolute paths and batch-relative names.</summary>
/// <remarks>Directory discovery filters video extensions; explicit files and globs are probed as requested.
/// Recursive discovery skips reparse entries to avoid following junctions and loops.</remarks>
public static class InputFiles
{
    private static readonly HashSet<string> videoTypes = [".mp4", ".mov", ".mkv", ".m4v", ".webm", ".avi", ".mts", ".m2ts", ".ts", ".mxf", ".mpg", ".mpeg", ".wmv"];
    private static string ExpandHome(string path) => path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : path.StartsWith("~/") || path.StartsWith(@"~\") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;
    public static (string Path, string Relative)[] Expand(CliArguments args)
    {
        var found = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string relative)
        {
            path = Path.GetFullPath(path);
            found.TryAdd(path, (path, relative));
        }
        foreach (var raw in args.Inputs)
        {
            var path = ExpandHome(raw);
            if (Directory.Exists(path))
            {
                foreach (var p in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = args.Has("recursive"), AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Order(StringComparer.OrdinalIgnoreCase))
                    if (videoTypes.Contains(Path.GetExtension(p).ToLowerInvariant()))
                        Add(p, Path.GetRelativePath(path, p));
            }
            // Brackets are legal in Windows filenames. An existing literal path
            // takes precedence over interpreting its characters as a glob.
            else if (File.Exists(path))
                Add(path, Path.GetFileName(path));
            else if (path.IndexOfAny(['*', '?', '[']) >= 0)
            {
                var recursive = path.Replace('\\', '/').Split('/').Contains("**");
                if (recursive && !args.Has("recursive"))
                    throw new CliException("invalid_arguments", "** 通配符需要 --recursive。");
                var absolute = Path.GetFullPath(path).Replace('\\', '/');
                // The longest existing directory is literal, even if its name
                // contains brackets. Interpret only the remaining suffix as a glob.
                var root = Path.GetDirectoryName(absolute)!;
                while (!Directory.Exists(root) && Path.GetDirectoryName(root) is { } parent)
                    root = parent;
                var relativeGlob = Path.GetRelativePath(root, absolute).Replace('\\', '/');
                var pattern = GlobPattern(relativeGlob);
                // A non-recursive glob only needs the directory levels present
                // in its remaining pattern. Do not inspect unrelated subtrees.
                var depth = recursive ? int.MaxValue : relativeGlob.Count(c => c == '/');
                var files = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = depth > 0,
                    MaxRecursionDepth = depth,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = false
                })
                    .Select(Path.GetFullPath).Where(p => pattern.IsMatch(Path.GetRelativePath(root, p).Replace('\\', '/'))).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
                var common = files.Length > 0 ? Path.GetDirectoryName(files[0])! : Directory.GetCurrentDirectory();
                foreach (var file in files)
                    while (Path.GetRelativePath(common, Path.GetDirectoryName(file)!).Split(Path.DirectorySeparatorChar)[0] == "..")
                        common = Path.GetDirectoryName(common)!;
                foreach (var file in files)
                    Add(file, Path.GetRelativePath(common, file));
            }
            else if (args.Has("continue_on_error"))
                Add(path, Path.GetFileName(path));
            else
                throw new CliException("input_unavailable", $"输入不存在：{raw}", 4);
        }
        if (found.Count == 0)
            throw new CliException("input_unavailable", "没有找到匹配的视频。", 4);
        if (args.Get("output") is not null && (found.Count != 1 || args.Get("output_dir") is not null))
            throw new CliException("invalid_arguments", "--output 需要单个输入，且不能与 --output-dir 同时使用。");
        return found.Values.ToArray();
    }
    private static Regex GlobPattern(string glob)
    {
        var pattern = new StringBuilder(@"\A");
        for (int i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*'
                && (i == 0 || glob[i - 1] == '/') && (i + 2 == glob.Length || glob[i + 2] == '/'))
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    pattern.Append("(?:.*/)?");
                    i++;
                }
                else
                    pattern.Append(".*");
            }
            else if (glob[i] == '*')
                pattern.Append("[^/]*");
            else if (glob[i] == '?')
                pattern.Append("[^/]");
            else if (glob[i] == '[' && CharacterClassEnd(glob, i) is var end && end >= 0)
            {
                var content = glob[(i + 1)..end];
                bool negate = content.StartsWith('!');
                if (negate)
                    content = content[1..];
                // Regex.Escape handles literal brackets and carets; hyphens
                // retain their conventional range meaning inside the class.
                pattern.Append("(?!/)[").Append(negate ? "^" : "");
                foreach (var c in content)
                    pattern.Append(c == '-' ? "-" : Regex.Escape(c.ToString()));
                pattern.Append(']');
                i = end;
            }
            else
                pattern.Append(Regex.Escape(glob[i].ToString()));
        }
        try
        {
            return new Regex(pattern.Append(@"\z").ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            throw new CliException("invalid_arguments", "Invalid glob: " + glob);
        }
    }
    private static int CharacterClassEnd(string glob, int opening)
    {
        int start = opening + 1;
        if (start < glob.Length && glob[start] == '!')
            start++;
        // A leading ']' is a member of the class, e.g. []] matches a literal ].
        if (start < glob.Length && glob[start] == ']')
            start++;
        return start < glob.Length ? glob.IndexOf(']', start) : -1;
    }
}
