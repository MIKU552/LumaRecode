using LumaRecode.Native;

internal static class OutputChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(parent, "LumaRecode-managed-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "原片.dat"), destination = Path.Combine(root, "日本語.mkv");
            File.WriteAllText(source, "source bytes");
            foreach (var invalid in new[] { "embedded\0NUL", "invalid\ud800surrogate", "" })
            {
                bool rejected = false;
                try
                {
                    using var unused = NativeOutputTransaction.Create(source, invalid);
                }
                catch (ArgumentException) { rejected = true; }
                check(rejected, "Invalid managed file path reached the native filesystem.");
            }
            check(NativeOutputTransaction.FilesSame(source, destination, out var same, out _) == NativeStatus.NotFound
                && !same, "Missing-file identity status differs.");
            using (var output = NativeOutputTransaction.Create(source, destination))
            {
                string stage = output.StagingPath, workspace = output.WorkspacePath;
                check(output.DestinationPath == destination && output.Snapshot.SourceSize == 12,
                    "Managed file transaction paths or source size differ.");
                File.WriteAllText(stage, "partial");
                check(output.Commit(out _) == NativeStatus.InvalidArgument, "Managed unsealed publication accepted.");
                check(output.Cleanup(false, out var diagnostic) == NativeStatus.IoError && diagnostic.Length > 0
                    && File.Exists(stage) && output.Snapshot.State == NativeOutputState.Retained,
                    "Managed cleanup confirmation was ignored.");
                check(output.Cleanup(true, out _) == NativeStatus.Ok && !Directory.Exists(workspace)
                    && output.Snapshot.State == NativeOutputState.Aborted, "Managed cleanup retry failed.");
            }
            File.WriteAllText(destination, "existing output");
            bool consentEnforced = false;
            try
            {
                using var unused = NativeOutputTransaction.Create(source, destination);
            }
            catch (NativeOutputException error) { consentEnforced = error.Status == NativeStatus.AlreadyExists; }
            check(consentEnforced && File.ReadAllText(destination) == "existing output", "Managed overwrite consent lost.");
            using var replacement = NativeOutputTransaction.Create(source, destination, overwrite: true);
            File.WriteAllText(replacement.StagingPath, "replacement");
            check(replacement.Seal(false, out _) == NativeStatus.InvalidArgument, "Managed unconfirmed seal accepted.");
            check(replacement.Seal(true, out _) == NativeStatus.Ok, "Managed verified seal failed.");
            check(replacement.Commit(out _) == NativeStatus.Ok && File.ReadAllText(destination) == "replacement"
                && replacement.Snapshot.State == NativeOutputState.Committed, "Managed replacement failed.");
        }
        finally
        {
            // These fixtures have no subprocesses and belong only to this check.
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.TrimEndingDirectorySeparator(parent))
            {
                throw new InvalidOperationException("Unexpected managed output test directory.");
            }

            Directory.Delete(root, recursive: true);
        }
    }
}
