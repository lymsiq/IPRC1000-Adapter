using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Web.Script.Serialization;
using IprcSetup2;

internal static class Program
{
    private static int passed;
    private static void Assert(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static byte[] Archive(string additional = null, bool corrupt = false, bool duplicate = false)
    {
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                var manifest = new PackageManifest { Product = Product.DirectoryName, Version = "0.1.0-preview.1", SelfContained = true, Files = new List<ManifestFile>() };
                foreach (var name in new[] { Product.ExeName, "assets/etw_consumer.exe" })
                {
                    var bytes = Encoding.UTF8.GetBytes("fixture " + name);
                    using (var stream = zip.CreateEntry(name).Open()) stream.Write(bytes, 0, bytes.Length);
                    using (var source = new MemoryStream(bytes)) manifest.Files.Add(new ManifestFile { Path = name, Size = bytes.Length, Sha256 = corrupt ? new string('0', 64) : SetupCore.Hash(source) });
                }
                if (additional != null) { using (var writer = new StreamWriter(zip.CreateEntry(additional).Open())) writer.Write("malicious"); }
                if (duplicate) { using (var writer = new StreamWriter(zip.CreateEntry(Product.ExeName.ToUpperInvariant()).Open())) writer.Write("duplicate"); }
                using (var writer = new StreamWriter(zip.CreateEntry("package-manifest.json").Open())) writer.Write(new JavaScriptSerializer().Serialize(manifest));
            }
            return memory.ToArray();
        }
    }
    private static void Reject(string root, string name, byte[] archive)
    {
        var target = Path.Combine(root, "reject-" + Guid.NewGuid().ToString("N"));
        var rejected = false;
        try { using (var input = new MemoryStream(archive)) SetupCore.Extract(input, target, null); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected && !Directory.Exists(target), name + " rejected before any extraction");
    }
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "dingge-setup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var parent = Path.Combine(root, "custom drive folder");
            var productPath = Path.Combine(parent, Product.DirectoryName);
            Assert(SetupCore.InstallationDirectory(parent) == productPath, "custom location gets dedicated product directory");
            Assert(SetupCore.InstallationDirectory(productPath + "\\") == productPath, "selected product directory is not duplicated");
            var relativeRejected = false;
            try { SetupCore.InstallationDirectory("relative-folder"); } catch (ArgumentException) { relativeRejected = true; }
            Assert(relativeRejected, "relative install location rejected");
            var driveRelativeRejected = false;
            try { SetupCore.InstallationDirectory("C:relative-folder"); } catch (ArgumentException) { driveRelativeRejected = true; }
            Assert(driveRelativeRejected, "drive-relative install location rejected");
            var rootRejected = false;
            try { SetupCore.ValidateInstallationDirectory(Path.GetPathRoot(root)); } catch (ArgumentException) { rootRejected = true; }
            Assert(rootRejected, "disk root cannot be an installation target");
            var uncRejected = false;
            try { SetupCore.InstallationDirectory(@"\\server\share"); } catch (ArgumentException) { uncRejected = true; }
            Assert(uncRejected, "network install location rejected");
            Directory.CreateDirectory(productPath);
            var personal = Path.Combine(productPath, "keep.txt");
            File.WriteAllText(personal, "untouched personal content");
            var occupiedRejected = false;
            try { SetupCore.ValidateInstallationDirectory(productPath); } catch (IOException) { occupiedRejected = true; }
            Assert(occupiedRejected && File.ReadAllText(personal) == "untouched personal content", "unmanaged existing directory rejected with data intact");
            var needsComponents = SetupCore.InstallationResult(new InstallState { RestartRequired = true });
            Assert(!needsComponents[1].Contains("重启后即可"), "missing components never promise ready after restart");
            var needsDoubao = SetupCore.InstallationResult(new InstallState { ComponentsReady = true, RestartRequired = true });
            Assert(needsDoubao[0].Contains("豆包") && !needsDoubao[1].Contains("重启后即可"), "missing Doubao shown separately from completion");
            var readyRestart = SetupCore.InstallationResult(new InstallState { ComponentsReady = true, DoubaoInstalled = true, RestartRequired = true });
            Assert(readyRestart[1].Contains("重启后即可") && readyRestart[1].Contains("豆包"), "verified reboot state retains Doubao configuration reminder");
            Reject(root, "traversal", Archive("../outside.txt"));
            Reject(root, "absolute path", Archive("C:/outside.txt"));
            Reject(root, "alternate data stream", Archive("safe.txt:payload"));
            Reject(root, "DOS device", Archive("NUL.txt"));
            Reject(root, "noncanonical separator", Archive("assets\\extra.txt"));
            Reject(root, "duplicate path differing case", Archive(null, false, true));
            Reject(root, "hash mismatch", Archive(null, true));
            Reject(root, "unmanifested file", Archive("unlisted.txt"));
            var target = Path.Combine(root, "path with spaces");
            using (var input = new MemoryStream(Archive())) SetupCore.Extract(input, target, null);
            Assert(File.Exists(Path.Combine(target, Product.ExeName)) && File.Exists(Path.Combine(target, "assets", "etw_consumer.exe")), "valid payload extracted");
            var statePath = Path.Combine(root, "state.json");
            SetupCore.SaveState(statePath, new InstallState { Phase = "initial" });
            SetupCore.SaveState(statePath, new InstallState { Phase = "repaired" });
            Assert(SetupCore.LoadState(statePath).Phase == "repaired" && SetupCore.LoadState(statePath + ".bak").Phase == "initial", "atomic state with previous backup");
            if (args.Length == 2 && args[0] == "--payload")
            {
                var real = Path.Combine(root, "real payload");
                using (var input = File.OpenRead(args[1])) SetupCore.Extract(input, real, null);
                Assert(File.Exists(Path.Combine(real, Product.ExeName)), "release payload full integrity and extraction");
            }
            Console.WriteLine(passed + " tests passed; no registry, driver, or installed application was changed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("dingge-setup-tests-")) Directory.Delete(root, true);
        }
    }
}
