using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using QuickNote.Core;

namespace QuickNote;

internal static class UpdateSmokeChecks
{
    internal static async Task RunParent(string workspace)
    {
        File.AppendAllText(Path.Combine(workspace, "trace.txt"), "parent entered\n");
        if (!UpdateCoordinator.IsWorkspaceFile(Path.Combine(workspace, "package.exe"), "package.exe"))
            throw new InvalidDataException("Invalid test workspace");
        var staged = Path.Combine(workspace, "package.exe");
        using var input = File.OpenRead(staged);
        var package = new UpdatePackage(UpdateCoordinator.CurrentVersion, UpdateCoordinator.Flavor ?? PackageFlavor.Small, "",
            new Uri("https://github.com/fuweiwe1/QuickNote/releases/download/v0.3.0/unused.exe"), input.Length,
            Convert.ToHexString(SHA256.HashData(input)));
        await UpdateCoordinator.StartHelper(workspace, package, smokeMode: true,
            failStartup: File.Exists(Path.Combine(workspace, "simulate-start-failure")));
    }

    internal static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        SmokeChecks.Assert(UpdateCoordinator.Flavor is not null, "Published package flavor metadata is missing");
        foreach (var broken in new[] { false, true })
        {
            var workspace = UpdateCoordinator.CreateWorkspace();
            try
            {
                var program = Path.Combine(workspace, "program");
                Directory.CreateDirectory(program);
                var target = Path.Combine(program, "QuickNote.exe");
                File.Copy(Environment.ProcessPath!, target);
                var staged = Path.Combine(workspace, "package.exe");
                File.Copy(Environment.ProcessPath!, staged);
                if (broken) File.WriteAllText(Path.Combine(workspace, "simulate-start-failure"), "fail startup deliberately");
                var notebook = Path.Combine(workspace, "notebook.json");
                File.WriteAllText(notebook, "便签和图片不参与程序更新");
                var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = program,
                    RedirectStandardError = true, RedirectStandardOutput = true };
                start.ArgumentList.Add("--update-parent-smoke"); start.ArgumentList.Add(workspace);
                using var parent = Process.Start(start) ?? throw new Exception("Cannot launch parent fixture");
                var errors = parent.StandardError.ReadToEndAsync();
                var messages = parent.StandardOutput.ReadToEndAsync();
                var parentClosed = false;
                var parentExitCode = -1;
                var finished = false;
                for (var attempt = 0; attempt < 400; attempt++)
                {
                    if (File.Exists(Path.Combine(workspace, "parent-error.txt")))
                        throw new Exception(File.ReadAllText(Path.Combine(workspace, "parent-error.txt")));
                    if (File.Exists(Path.Combine(workspace, broken ? "error.txt" : "complete"))) { finished = true; break; }
                    if (!broken && File.Exists(Path.Combine(workspace, "error.txt")))
                        throw new Exception(File.ReadAllText(Path.Combine(workspace, "error.txt")));
                    if (!parentClosed && parent.HasExited)
                    {
                        parentExitCode = parent.ExitCode;
                        if (!File.Exists(Path.Combine(workspace, "ready")))
                            throw new Exception($"Parent fixture exited before helper readiness ({parentExitCode}).\n" + await errors + await messages);
                        parent.Dispose();
                        parentClosed = true;
                    }
                    await Task.Delay(100);
                }
                if (!finished)
                {
                    var trace = Path.Combine(workspace, "trace.txt");
                    throw new Exception("Update handoff timed out. Parent exited: " + (parentClosed || parent.HasExited) + "; files: " +
                        string.Join(",", Directory.GetFiles(workspace).Select(Path.GetFileName)) + "\n" +
                        (File.Exists(trace) ? File.ReadAllText(trace) : "no child trace"));
                }
                if (!parentClosed) { await parent.WaitForExitAsync(); parentExitCode = parent.ExitCode; }
                SmokeChecks.Assert(parentExitCode == 0, "Parent did not exit cleanly");
                using var original = File.OpenRead(Environment.ProcessPath!);
                using var result = File.OpenRead(target);
                SmokeChecks.Assert(SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(result)), "Installed or restored executable differs");
                SmokeChecks.Assert(File.ReadAllText(notebook) == "便签和图片不参与程序更新", "Update touched notes");
                SmokeChecks.Assert(File.Exists(target + (broken ? ".failed" : ".previous")), "Missing rollback or backup evidence");
            }
            finally
            {
                // The helper may still be returning through its own executable; leave locked files for later cleanup.
                await Task.Delay(500);
                try { Directory.Delete(workspace, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        File.WriteAllText(Path.Combine(output, "passed.txt"),
            "PASS real parent/helper exit handoff\nPASS replace locked running executable after parent exit\nPASS restart and healthy-start handshake\nPASS rollback when replacement cannot launch\nPASS notes remain untouched\n");
    }
}
