using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChatAutoUpdater;

static class Program
{
    public const string DllName = "MultiplayerChat.dll";
    public const string PdbName = "MultiplayerChat.pdb";

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Args: [0] = Beat Saber install path, [1] = Process ID to kill (optional)
        var beatSaberPath = args.Length > 0 ? args[0] : GetBeatSaberPathFromExe();
        var processId = args.Length > 1 && int.TryParse(args[1], out var pid) ? pid : (int?)null;

        if (processId.HasValue)
        {
            try
            {
                var proc = Process.GetProcessById(processId.Value);
                proc.Kill();
                proc.WaitForExit(5000);
            }
            catch { /* ignore */ }
        }
        else
        {
            foreach (var proc in Process.GetProcessesByName("Beat Saber"))
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
            }
        }

        Application.Run(new UpdaterForm(beatSaberPath));
    }

    private static string GetBeatSaberPathFromExe()
    {
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var pluginsDir = Path.GetDirectoryName(exePath);
        var beatSaberRoot = pluginsDir != null ? Path.GetDirectoryName(pluginsDir) : null;
        return beatSaberRoot ?? Environment.CurrentDirectory;
    }
}

public class UpdaterForm : Form
{
    private readonly string _beatSaberPath;

    /// <summary>Shipped CAU builds pin one Multiplayer Chat GitHub release tag for predictable installs.</summary>
    private const string TargetReleaseTag = "v0.3.1";

    private static readonly string ReleasesPageUrl =
        $"https://github.com/buttheadicus/BeatSaber-Multiplayer-Chat/releases/tag/{TargetReleaseTag}";

    private static readonly string TaggedReleaseApi =
        $"https://api.github.com/repos/buttheadicus/BeatSaber-Multiplayer-Chat/releases/tags/{TargetReleaseTag}";

    private static readonly Regex VersionedModZipFileRegex = new(
        @"MultiplayerChat-(\d+)\.(\d+)\.(\d+)\.zip",
        RegexOptions.IgnoreCase);

    public UpdaterForm(string beatSaberPath)
    {
        _beatSaberPath = beatSaberPath ?? Environment.CurrentDirectory;
        Text = "Multiplayer Chat Updater";
        Size = new System.Drawing.Size(400, 180);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var label = new Label
        {
            Text = "Multiplayer Chat has an update.",
            AutoSize = true,
            Location = new System.Drawing.Point(20, 20),
            Font = new System.Drawing.Font("Segoe UI", 12F)
        };

        var versionInfoBtn = new Button
        {
            Text = "Version Info",
            Location = new System.Drawing.Point(20, 70),
            Size = new System.Drawing.Size(160, 35)
        };
        versionInfoBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true }); } catch { }
        };

        var installBtn = new Button
        {
            Text = "Install",
            Location = new System.Drawing.Point(200, 70),
            Size = new System.Drawing.Size(160, 35)
        };
        installBtn.Click += async (_, _) =>
        {
            installBtn.Enabled = false;
            installBtn.Text = "Installing...";
            try
            {
                await DownloadAndInstallAsync();
                MessageBox.Show("Update installed successfully! You can now launch Beat Saber.", "Success", MessageBoxButtons.OK);
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Install failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                installBtn.Enabled = true;
                installBtn.Text = "Install";
            }
        };

        Controls.Add(label);
        Controls.Add(versionInfoBtn);
        Controls.Add(installBtn);
    }

    private async Task DownloadAndInstallAsync()
    {
        // GitHub API + releases require TLS 1.2+
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        var tempZip = Path.Combine(Path.GetTempPath(), "MultiplayerChat-Update-" + Guid.NewGuid().ToString("N") + ".zip");
        var extractDir = Path.Combine(Path.GetTempPath(), "MultiplayerChat-Update-Extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var client = new WebClient();
            client.Headers.Set("User-Agent", "MultiplayerChat-CAU");
            client.Headers.Set("Accept", "application/vnd.github.v3+json");

            var json = await client.DownloadStringTaskAsync(TaggedReleaseApi);
            var zipUrl = PickBestModReleaseZipUrl(json);
            if (string.IsNullOrEmpty(zipUrl))
                throw new Exception(
                    "No suitable mod release zip found. Upload MultiplayerChat-X.Y.Z.zip or MultiplayerChat.zip as a release asset (not CAU, not GitHub's Source code archive).");

            var zipBytes = await client.DownloadDataTaskAsync(zipUrl);
            File.WriteAllBytes(tempZip, zipBytes);

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(tempZip, extractDir);

            var dllSourcePath = FindFileRecursive(extractDir, Program.DllName);
            if (string.IsNullOrEmpty(dllSourcePath))
                throw new Exception($"{Program.DllName} not found inside the release zip. Release layout may have changed.");

            var sourceDir = Path.GetDirectoryName(dllSourcePath);
            if (string.IsNullOrEmpty(sourceDir))
                throw new Exception("Invalid path to extracted DLL.");

            var pluginsDest = Path.Combine(_beatSaberPath, "Plugins");
            if (!Directory.Exists(pluginsDest))
                Directory.CreateDirectory(pluginsDest);

            // 1) Remove current plugin binaries (unlocked: game was killed)
            foreach (var name in new[] { Program.DllName, Program.PdbName })
            {
                var existing = Path.Combine(pluginsDest, name);
                if (File.Exists(existing))
                {
                    try { File.Delete(existing); }
                    catch (Exception ex)
                    {
                        throw new Exception($"Could not delete {name}: {ex.Message}. Close Beat Saber / file handles and retry.");
                    }
                }
            }

            // 2) Copy new DLL + PDB from the folder that contained the DLL in the archive
            var destDll = Path.Combine(pluginsDest, Program.DllName);
            File.Copy(dllSourcePath, destDll, false);

            var pdbSource = Path.Combine(sourceDir, Program.PdbName);
            if (File.Exists(pdbSource))
            {
                var destPdb = Path.Combine(pluginsDest, Program.PdbName);
                File.Copy(pdbSource, destPdb, false);
            }
        }
        finally
        {
            TryDelete(tempZip);
            TryDeleteDir(extractDir);
        }
    }

    /// <summary>Mod release zip only (matches MultiplayerChat GitHubReleaseVersion rules), highest semver.</summary>
    private static string PickBestModReleaseZipUrl(string json)
    {
        var urls = ExtractReleaseZipUrls(json).Where(IsModMultiplayerChatZipUrl).ToList();
        if (urls.Count == 0)
            return string.Empty;

        string bestUrl = null;
        string bestVer = null;

        foreach (var url in urls)
        {
            var fn = GetLastUrlPathSegment(url);
            string v = null;
            var vm = VersionedModZipFileRegex.Match(fn);
            if (vm.Success)
                v = $"{vm.Groups[1].Value}.{vm.Groups[2].Value}.{vm.Groups[3].Value}";
            else if (TryParseVersionFromMultiplayerChatZipUrl(url, out var fromTag))
                v = fromTag;

            if (v == null) continue;
            if (bestVer == null || CompareSemver(v, bestVer) > 0)
            {
                bestVer = v;
                bestUrl = url;
            }
        }

        return bestUrl ?? string.Empty;
    }

    private static IEnumerable<string> ExtractReleaseZipUrls(string json)
    {
        foreach (Match m in Regex.Matches(json, @"""browser_download_url""\s*:\s*""(https://[^""]+\.zip)""",
                     RegexOptions.IgnoreCase))
            yield return m.Groups[1].Value;
    }

    private static bool IsModMultiplayerChatZipUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (url.IndexOf("/releases/download/", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        if (url.IndexOf("/archive/", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (url.IndexOf("codeload.github.com", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        var fn = GetLastUrlPathSegment(url);
        if (string.IsNullOrEmpty(fn)) return false;

        if (fn.Equals("CAU.zip", StringComparison.OrdinalIgnoreCase)) return false;
        if (fn.Equals("CAU.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (fn.Equals("Chat.Auto.Updater.CAU.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (fn.Equals("Chat Auto Updater (CAU).exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (fn.StartsWith("Source code", StringComparison.OrdinalIgnoreCase)) return false;

        if (fn.Equals("MultiplayerChat.zip", StringComparison.OrdinalIgnoreCase)) return true;
        return VersionedModZipFileRegex.IsMatch(fn);
    }

    private static string GetLastUrlPathSegment(string url)
    {
        try
        {
            var i = url.LastIndexOf('?');
            var path = i >= 0 ? url.Substring(0, i) : url;
            var slash = path.LastIndexOf('/');
            if (slash < 0 || slash >= path.Length - 1) return "";
            return Uri.UnescapeDataString(path.Substring(slash + 1));
        }
        catch
        {
            return "";
        }
    }

    private static bool TryParseVersionFromMultiplayerChatZipUrl(string url, out string version)
    {
        version = "";
        if (!GetLastUrlPathSegment(url).Equals("MultiplayerChat.zip", StringComparison.OrdinalIgnoreCase))
            return false;

        var m = Regex.Match(url, @"/releases/download/([^/]+)/MultiplayerChat\.zip", RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        var tag = m.Groups[1].Value.Trim();
        if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) && tag.Length > 1)
            tag = tag.Substring(1);
        if (!Regex.IsMatch(tag, @"^\d+\.\d+\.\d+$")) return false;
        version = tag;
        return true;
    }

    private static int CompareSemver(string a, string b)
    {
        var ap = a.Split('.');
        var bp = b.Split('.');
        for (var i = 0; i < Math.Max(ap.Length, bp.Length); i++)
        {
            var av = i < ap.Length && int.TryParse(ap[i], out var x) ? x : 0;
            var bv = i < bp.Length && int.TryParse(bp[i], out var y) ? y : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    private static string FindFileRecursive(string root, string fileName)
    {
        try
        {
            foreach (var path in Directory.GetFiles(root, fileName, SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
        }
        catch { /* ignore inaccessible dirs */ }

        return string.Empty;
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* ignore */ }
    }

    private static void TryDeleteDir(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { /* ignore */ }
    }
}
