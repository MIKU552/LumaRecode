using System.Text.Json.Nodes;
using VideoConverter;
using VideoConverter.Core;

/// <summary>Focused regressions for numeric parsing, CLI errors and filesystem discovery.</summary>
internal static class CodeChecks
{
    public static int Run()
    {
        var failures = new List<string>();
        int count = 0;
        void Check(bool condition, string name)
        {
            count++;
            if (!condition)
                failures.Add(name);
        }

        Check(JsonFields.Number("30000/1001") == 30000d / 1001, "rational frame rate");
        foreach (var text in new[] { "NaN", "Infinity", "1/0", "bad/2", "1e308/1e-308", "1/2/3" })
            Check(JsonFields.Number(text, 7) == 7, "invalid or overflowing number: " + text);

        try
        {
            CliArguments.Parse(["doctor", "--ffmpeg", "--json"]);
            Check(false, "missing option value must not consume --json");
        }
        catch (CliException e)
        {
            Check(e.Code == "invalid_arguments" && e.Status == 2, "missing option value classification");
        }
        Check(CliArguments.Parse(["convert", "--duration", "-1", "clip.mp4"]).Duration == -1, "negative numeric option reaches duration validation");
        var literal = CliArguments.Parse(["probe", "--", "--help"]);
        Check(!literal.Help && literal.Inputs.SequenceEqual(["--help"]), "literal option-like filename");

        var catalog = new TranslationCatalog(new Dictionary<string, string>
        {
            ["无效的 {0}：{1}"] = "Invalid {0}: {1}",
            ["重复 {0} / {0}"] = "Repeated {0} / {0}"
        });
        Check(catalog.Translate("无效的 sampling：", "en") == "Invalid sampling: ", "empty translation argument");
        Check(catalog.Translate("重复 one / two", "en") == "重复 one / two", "repeated translation placeholders must agree");

        var directory = Directory.CreateTempSubdirectory("LumaRecode-code-check-");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var bin = Path.Combine(directory.FullName, "bin");
            Directory.CreateDirectory(bin);
            var executable = Path.Combine(bin, "sample-tool.exe");
            File.WriteAllText(executable, "");
            Environment.SetEnvironmentVariable("PATH", bin);
            Check(ProcessRunner.FindTool("sample-tool").Equals(executable, StringComparison.OrdinalIgnoreCase), "extension-free PATH lookup");
            Check(ProcessRunner.FindTool("sample-tool.exe").Equals(executable, StringComparison.OrdinalIgnoreCase), "explicit .exe PATH lookup");
            Environment.SetEnvironmentVariable("PATH", oldPath);

            using (var report = new CliReporter(json: true, verbose: false, text => text))
            {
                try
                {
                    report.OpenLog(directory.FullName);
                    Check(false, "directory log path must fail");
                }
                catch (CliException e)
                {
                    Check(e.Code == "log_unavailable" && e.Status == 5, "directory log failure classification");
                }
                catch (UnauthorizedAccessException)
                {
                    Check(false, "directory log failure classification");
                }
            }

            var root = Path.Combine(directory.FullName, "input");
            Directory.CreateDirectory(Path.Combine(root, "nested", "deeper"));
            foreach (var relative in new[] { "a.mp4", "b.mp4", "].mp4", "nested/c.mp4", "nested/deeper/d.mp4" })
                File.WriteAllText(Path.Combine(root, relative), "");
            foreach (var (pattern, expected, recursive) in new[]
            {
                ("*.mp4", new[] { "].mp4", "a.mp4", "b.mp4" }, false),
                ("[a-b].mp4", new[] { "a.mp4", "b.mp4" }, false),
                ("[]].mp4", new[] { "].mp4" }, false),
                ("nested/*.mp4", new[] { "c.mp4" }, false),
                ("n*/*.mp4", new[] { "c.mp4" }, false),
                ("[!a].mp4", new[] { "].mp4", "b.mp4" }, false),
                ("**/*.mp4", new[] { "].mp4", "a.mp4", "b.mp4", "c.mp4", "d.mp4" }, true)
            })
            {
                var words = new List<string> { "convert", Path.Combine(root, pattern) };
                if (recursive)
                    words.Add("--recursive");
                try
                {
                    var actual = InputFiles.Expand(CliArguments.Parse(words.ToArray())).Select(p => Path.GetFileName(p.Path)).Order().ToArray();
                    Check(actual.SequenceEqual(expected.Order()), "glob: " + pattern);
                }
                catch (CliException)
                {
                    Check(false, "glob: " + pattern);
                }
            }

            var literalPath = Path.Combine(root, "clip[1].mp4");
            File.WriteAllText(literalPath, "");
            Check(InputFiles.Expand(CliArguments.Parse(["convert", literalPath])).Single().Path == literalPath,
                "existing bracketed filename must be literal");
            var bracketDirectory = Path.Combine(root, "folder[1]");
            Directory.CreateDirectory(bracketDirectory);
            File.WriteAllText(Path.Combine(bracketDirectory, "inside.mp4"), "");
            Check(InputFiles.Expand(CliArguments.Parse(["convert", Path.Combine(bracketDirectory, "*.mp4")])).Single().Path
                == Path.Combine(bracketDirectory, "inside.mp4"), "literal bracketed directory in a glob");
            var configArguments = CliArguments.Parse(["plan", literalPath, "--config", directory.FullName]);
            try
            {
                configArguments.EncodingValues([]);
                Check(false, "directory config path must fail");
            }
            catch (CliException e)
            {
                Check(e.Code == "invalid_config", "directory config error classification");
            }

            Check(MediaProbe.ReadHexDump("00000000: 0102 03ff  ....").SequenceEqual(new byte[] { 1, 2, 3, 255 }), "ffprobe hex dump excludes ASCII column");
            foreach (var payload in new[] { new byte[] { 0 }, new byte[] { 10, 4, 1 }, new byte[] { 8, 128 } })
            {
                try
                {
                    MediaProbe.ProtobufFields(payload);
                    Check(false, "truncated protobuf rejection");
                }
                catch (InvalidDataException)
                {
                    Check(true, "truncated protobuf rejection");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            // Only this test's newly created directory may be removed.
            var relative = Path.GetRelativePath(Path.GetFullPath(Path.GetTempPath()), directory.FullName);
            if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) || !directory.Name.StartsWith("LumaRecode-code-check-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test directory.");
            Directory.Delete(directory.FullName, recursive: true);
        }

        foreach (var failure in failures)
            Console.Error.WriteLine("FAIL " + failure);
        Console.WriteLine($"Checked {count} numeric, argument, translation and filesystem regressions; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 1;
    }
}
