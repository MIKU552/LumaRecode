using System.Buffers.Binary;
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
            {
                failures.Add(name);
            }
        }

        Check(FrameTimeline.TryReadPoint("packet|pts=-1001|duration=1001|flags=K_", true, out var point, out var discarded)
            && point == new FramePoint(-1001, 1001, true) && !discarded, "packet index tick parsing");
        Check(FrameTimeline.TryReadPoint("frame|key_frame=1|best_effort_timestamp=9223372036854775806|duration=N/A", false, out point, out discarded)
            && point == new FramePoint(long.MaxValue - 1, 0, true), "frame index optional duration and 64-bit timestamp");
        Check(!FrameTimeline.TryReadPoint("packet|pts=N/A|flags=_D", true, out _, out discarded) && discarded, "discarded preroll has no required PTS");
        Check(!FrameTimeline.TryReadPoint("packet|dts=30|duration=1|other=pts=30", true, out _, out _), "missing PTS forces decoded index fallback");
        Check(!FrameTimeline.TryReadPoint("packet|pts=9223372036854775808", true, out _, out _), "overflowing frame timestamp is rejected");
        FramePoint[] original = [new(0, 1, true), new(1, 1, false)];
        var immutable = new FrameTimeline(original, 1, 30, true);
        original[0] = new(90, 1, true);
        Check(immutable.Seconds(0) == 0, "timeline owns its timestamp snapshot");

        Check(JsonFields.Number("30000/1001") == 30000d / 1001, "rational frame rate");
        foreach (var text in new[] { "NaN", "Infinity", "1/0", "bad/2", "1e308/1e-308", "1/2/3" })
        {
            Check(JsonFields.Number(text, 7) == 7, "invalid or overflowing number: " + text);
        }

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
            {
                File.WriteAllText(Path.Combine(root, relative), "");
            }

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
                {
                    words.Add("--recursive");
                }

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
            var rawHeader = new byte[86];
            void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(rawHeader.AsSpan(offset, 2), value);
            void F32(int offset, float value) => BinaryPrimitives.WriteInt32BigEndian(rawHeader.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
            BinaryPrimitives.WriteUInt32BigEndian(rawHeader, 86);
            "prrf"u8.CopyTo(rawHeader.AsSpan(4));
            U16(8, 78);
            U16(16, 16);
            U16(18, 16);
            U16(26, 65279);
            F32(28, 2);
            F32(32, 0.5f);
            F32(36, 1);
            F32(52, 1);
            F32(68, 1);
            F32(72, 1);
            using (var rawFile = new FileStream(Path.Combine(root, "raw-header.bin"), FileMode.Create, FileAccess.ReadWrite))
            {
                rawFile.Write(rawHeader);
                var parsed = RawDevelopment.ReadHeader(rawFile, 0, 86);
                var coefficients = parsed["matrix"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                Check(Math.Abs(coefficients[0] - 1.716651187971268 * 2 * 65535 / 65279 / 1024) < 1e-12, "RAW WB and camera-to-target matrix");
                for (int row = 0; row < 3; row++)
                {
                    Check(Math.Abs(coefficients.Skip(row * 4).Take(3).Sum() * 256 / 65535 + coefficients[row * 4 + 3]) < 1e-15, "RAW black normalization " + row);
                }
                rawHeader[4] = 0;
                rawFile.Position = 0;
                rawFile.Write(rawHeader);
                try
                {
                    RawDevelopment.ReadHeader(rawFile, 0, 86);
                    Check(false, "invalid RAW magic rejection");
                }
                catch (InvalidDataException) { Check(true, "invalid RAW magic rejection"); }
            }
            foreach (var (a, b, c, d, expected) in new (int, int, int, int, string)[]
            {
                (1, 0, 0, 1, ""), (-1, 0, 0, 1, "hflip"), (1, 0, 0, -1, "vflip"), (-1, 0, 0, -1, "hflip,vflip"),
                (0, -1, 1, 0, "transpose=cclock"), (0, 1, -1, 0, "transpose=clock"),
                (0, 1, 1, 0, "transpose=cclock_flip"), (0, -1, -1, 0, "transpose=clock_flip")
            })
            {
                var video = new JsonObject
                {
                    ["side_data_list"] = new JsonArray(new JsonObject
                    {
                        ["side_data_type"] = "Display Matrix",
                        ["displaymatrix"] = $"00000000: {a * 65536} {b * 65536} 0\n00000001: {c * 65536} {d * 65536} 0\n00000002: 0 0 1073741824"
                    })
                };
                var media = new MediaInfo("source.mov", video, [], new(), "raw", null, "", 1, [], new());
                Check(string.Join(',', RawDevelopment.Orientation(media)) == expected, "RAW orientation " + expected);
            }
            foreach (var payload in new[] { new byte[] { 0 }, new byte[] { 10, 4, 1 }, new byte[] { 8, 128 } })
            {
                try
                {
                    var status = LumaRecode.Native.NativeMedia.ParseDjiColor(payload, out _, out _);
                    if (status != LumaRecode.Native.NativeStatus.Ok)
                    {
                        throw new InvalidDataException();
                    }

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
            {
                throw new InvalidOperationException("Unexpected test directory.");
            }

            Directory.Delete(directory.FullName, recursive: true);
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine("FAIL " + failure);
        }

        Console.WriteLine($"Checked {count} numeric, argument, translation and filesystem regressions; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 1;
    }
}
