using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Deceive.Properties;

namespace Deceive;

internal class MainController : ApplicationContext
{
    internal MainController()
    {
        // Accept self-signed certs for LCU API (localhost only).
        ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        TrayIcon = new NotifyIcon
        {
            Icon = Resources.DeceiveIcon,
            Visible = true,
            BalloonTipTitle = StartupHandler.DeceiveTitle,
            BalloonTipText = "Deceive is starting. Waiting for League client..."
        };
        TrayIcon.ShowBalloonTip(5000);

        LoadStatus();
        UpdateTray();
    }

    private NotifyIcon TrayIcon { get; }
    public bool Enabled { get; set; } = true;
    public string Status { get; set; } = null!;
    private string StatusFile { get; } = Path.Combine(Persistence.DataDir, "status");
    private LcuClient? LcuClient { get; set; }
    private CancellationTokenSource MonitorCts { get; } = new();
    private bool Connected { get; set; }

    private ToolStripMenuItem EnabledMenuItem { get; set; } = null!;
    private ToolStripMenuItem ChatStatus { get; set; } = null!;
    private ToolStripMenuItem OfflineStatus { get; set; } = null!;
    private ToolStripMenuItem MobileStatus { get; set; } = null!;

    /// <summary>
    /// Starts the background loop that detects the League client and keeps presence overridden.
    /// </summary>
    internal void StartMonitoring()
    {
        Task.Run(MonitorLoopAsync);
    }

    private async Task MonitorLoopAsync()
    {
        while (!MonitorCts.IsCancellationRequested)
        {
            try
            {
                if (LcuClient == null)
                {
                    var client = LcuClient.TryCreate();
                    if (client != null)
                    {
                        LcuClient = client;
                        Connected = true;

                        Trace.WriteLine("LCU connected. Setting initial presence.");

                        if (Enabled)
                            await client.SetAvailabilityAsync(Status);

                        TrayIcon.BalloonTipText = "Deceive is active! You are appearing " + Status + ".";
                        TrayIcon.ShowBalloonTip(3000);
                    }
                }
                else
                {
                    if (!await LcuClient.IsAliveAsync())
                    {
                        Trace.WriteLine("LCU disconnected. Waiting for reconnect...");
                        LcuClient.Dispose();
                        LcuClient = null;
                        Connected = false;
                    }
                    else if (Enabled)
                    {
                        // Continuously re-apply presence override. Game events (queue, champ select,
                        // in-game) change presence, so we keep pushing our desired status.
                        await LcuClient.SetAvailabilityAsync(Status);
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("Monitor loop error: " + e.Message);
            }

            try
            {
                await Task.Delay(2000, MonitorCts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task ApplyStatusAsync(string newStatus)
    {
        Status = newStatus;
        SaveStatus();

        if (LcuClient != null && Enabled)
            await LcuClient.SetAvailabilityAsync(newStatus);

        var message = newStatus == "chat"
            ? "You are now appearing online."
            : "You are now appearing " + newStatus + ".";

        TrayIcon.BalloonTipText = message;
        TrayIcon.ShowBalloonTip(2000);
    }

    private void UpdateTray()
    {
        var aboutMenuItem = new ToolStripMenuItem(StartupHandler.DeceiveTitle) { Enabled = false };

        var connectionText = Connected ? "Connected to League client" : "Waiting for League client...";
        var connectionMenuItem = new ToolStripMenuItem(connectionText) { Enabled = false };

        EnabledMenuItem = new ToolStripMenuItem("Enabled", null, async (_, _) =>
        {
            Enabled = !Enabled;
            if (LcuClient != null)
                await LcuClient.SetAvailabilityAsync(Enabled ? Status : "chat");

            TrayIcon.BalloonTipText = Enabled
                ? "Deceive enabled. Appearing " + Status + "."
                : "Deceive disabled. Appearing online.";
            TrayIcon.ShowBalloonTip(2000);
            UpdateTray();
        })
        { Checked = Enabled };

        ChatStatus = new ToolStripMenuItem("Online", null, async (_, _) =>
        {
            await ApplyStatusAsync("chat");
            Enabled = true;
            UpdateTray();
        })
        { Checked = Status.Equals("chat") };

        OfflineStatus = new ToolStripMenuItem("Offline", null, async (_, _) =>
        {
            await ApplyStatusAsync("offline");
            Enabled = true;
            UpdateTray();
        })
        { Checked = Status.Equals("offline") };

        MobileStatus = new ToolStripMenuItem("Mobile", null, async (_, _) =>
        {
            await ApplyStatusAsync("mobile");
            Enabled = true;
            UpdateTray();
        })
        { Checked = Status.Equals("mobile") };

        var typeMenuItem = new ToolStripMenuItem("Status Type", null, ChatStatus, OfflineStatus, MobileStatus);

        var restartItem = new ToolStripMenuItem("Restart and launch a different game", null, (_, _) =>
        {
            var result = MessageBox.Show(
                "Restart Deceive to launch a different game? This will also stop related games if they are running.",
                StartupHandler.DeceiveTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1
            );

            if (result is not DialogResult.Yes)
                return;

            Shutdown();
            Persistence.SetDefaultLaunchGame(LaunchGame.Prompt);
            Process.Start(Application.ExecutablePath);
            Environment.Exit(0);
        });

        var quitMenuItem = new ToolStripMenuItem("Quit", null, (_, _) =>
        {
            var result = MessageBox.Show(
                "Are you sure you want to stop Deceive? This will also stop related games if they are running.",
                StartupHandler.DeceiveTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1
            );

            if (result is not DialogResult.Yes)
                return;

            Shutdown();
            Application.Exit();
        });

        TrayIcon.ContextMenuStrip = new ContextMenuStrip();
        TrayIcon.ContextMenuStrip.Items.AddRange(new ToolStripItem[]
        {
            aboutMenuItem, connectionMenuItem, EnabledMenuItem, typeMenuItem, restartItem, quitMenuItem
        });
    }

    private void Shutdown()
    {
        MonitorCts.Cancel();
        // Restore normal presence before killing processes.
        if (LcuClient != null)
        {
            try { LcuClient.SetAvailabilityAsync("chat").Wait(2000); }
            catch { /* best effort */ }
            LcuClient.Dispose();
        }
        SaveStatus();
        Utils.KillProcesses();
    }

    private void LoadStatus()
    {
        if (File.Exists(StatusFile))
            Status = File.ReadAllText(StatusFile) == "mobile" ? "mobile" : "offline";
        else
            Status = "offline";
    }

    private void SaveStatus() => File.WriteAllText(StatusFile, Status);
}
