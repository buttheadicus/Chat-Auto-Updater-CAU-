using System;
using System.Diagnostics;
using System.IO;
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
    private const string TargetReleaseTag = "v0.3.4";

    private static readonly string ReleasesPageUrl =
        $"https://github.com/buttheadicus/BeatSaber-Multiplayer-Chat/releases/tag/{TargetReleaseTag}";

    private static readonly string TaggedReleaseApi =
        $"https://api.github.com/repos/buttheadicus/BeatSaber-Multiplayer-Chat/releases/tags/{TargetReleaseTag}";

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
                MessageBox.Show("Update installed successfully! You can now launch Beat Saber.", "Success",
                    MessageBoxButtons.OK);
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Install failed: {ex.Message}", "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
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
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        var tempDll = Path.Combine(Path.GetTempPath(), "MultiplayerChat-Update-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            using var client = new WebClient();
            client.Headers.Set("User-Agent", "MultiplayerChat-CAU");
            client.Headers.Set("Accept", "application/vnd.github.v3+json");

            var json = await client.DownloadStringTaskAsync(TaggedReleaseApi);

            if (!TryGetAssetBrowserUrl(json, Program.DllName, out var dllUrl))
                throw new Exception(
                    $"No {Program.DllName} attached to GitHub release {TargetReleaseTag}. Upload the DLL as a release asset.");

            File.WriteAllBytes(tempDll, await client.DownloadDataTaskAsync(dllUrl));

            byte[] pdbData = null;
            if (TryGetAssetBrowserUrl(json, Program.PdbName, out var pdbUrl) && !string.IsNullOrEmpty(pdbUrl))
                pdbData = await client.DownloadDataTaskAsync(pdbUrl);

            var pluginsDest = Path.Combine(_beatSaberPath, "Plugins");
            if (!Directory.Exists(pluginsDest))
                Directory.CreateDirectory(pluginsDest);

            foreach (var name in new[] { Program.DllName, Program.PdbName })
            {
                var existing = Path.Combine(pluginsDest, name);
                if (File.Exists(existing))
                {
                    try { File.Delete(existing); }
                    catch (Exception ex)
                    {
                        throw new Exception(
                            $"Could not delete {name}: {ex.Message}. Close Beat Saber / file handles and retry.");
                    }
                }
            }

            var destDll = Path.Combine(pluginsDest, Program.DllName);
            File.Copy(tempDll, destDll, false);

            if (pdbData != null && pdbData.Length > 0)
            {
                var destPdb = Path.Combine(pluginsDest, Program.PdbName);
                File.WriteAllBytes(destPdb, pdbData);
            }
        }
        finally
        {
            TryDelete(tempDll);
        }
    }

    private static bool TryGetAssetBrowserUrl(string json, string assetFileName, out string url)
    {
        url = "";
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(assetFileName))
            return false;

        var namePattern = "\"name\"\\s*:\\s*\"" + Regex.Escape(assetFileName) + "\"";
        var nameRx = new Regex(namePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var urlRx = new Regex("\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (Match nameMatch in nameRx.Matches(json))
        {
            var i = nameMatch.Index;
            var start = Math.Max(0, i - 2800);
            var end = Math.Min(json.Length, i + 2800);
            var window = json.Substring(start, end - start);
            var nameRel = i - start;

            // Prefer URL after this "name" (when JSON lists name before browser_download_url).
            var afterName = urlRx.Match(window, nameRel);
            if (afterName.Success)
            {
                url = afterName.Groups[1].Value;
                return url.Length > 0;
            }

            // Else GitHub often puts browser_download_url before name: use last URL before this name in the window.
            Match bestBefore = null;
            foreach (Match um in urlRx.Matches(window))
            {
                if (um.Index < nameRel && (bestBefore == null || um.Index > bestBefore.Index))
                    bestBefore = um;
            }

            if (bestBefore != null)
            {
                url = bestBefore.Groups[1].Value;
                return url.Length > 0;
            }
        }

        return false;
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
}
