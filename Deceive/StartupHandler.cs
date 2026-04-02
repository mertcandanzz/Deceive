using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Deceive;

internal static class StartupHandler
{
    public static string DeceiveTitle => "Deceive " + Utils.DeceiveVersion;

    // Arguments are parsed through System.CommandLine.DragonFruit.
    /// <param name="args">The game to be launched, or automatically determined if not passed.</param>
    /// <param name="gamePatchline">The patchline to be used for launching the game.</param>
    /// <param name="riotClientParams">Any extra parameters to be passed to the Riot Client.</param>
    /// <param name="gameParams">Any extra parameters to be passed to the launched game.</param>
    [STAThread]
    public static async Task Main(LaunchGame args = LaunchGame.Auto, string gamePatchline = "live", string? riotClientParams = null, string? gameParams = null)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
        Application.EnableVisualStyles();
        Trace.Listeners.Add(new ConsoleTraceListener());
        try
        {
            await StartDeceiveAsync(args, gamePatchline, riotClientParams, gameParams);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            // Show some kind of message so that Deceive doesn't just disappear.
            MessageBox.Show(
                "Deceive encountered an error and couldn't properly initialize itself. " +
                "Please contact the creator through GitHub (https://github.com/molenzwiebel/Deceive) or Discord.\n\n" + ex,
                DeceiveTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1
            );
        }
    }

    /// Actual main function. Wrapped into a separate function so we can catch exceptions.
    private static async Task StartDeceiveAsync(LaunchGame game, string gamePatchline, string? riotClientParams, string? gameParams)
    {
        // Refuse to do anything if the client is already running, unless we're specifically
        // allowing that through League/RC's --allow-multiple-clients.
        if (Utils.IsClientRunning() && !(riotClientParams?.Contains("allow-multiple-clients") ?? false))
        {
            var result = MessageBox.Show(
                "The Riot Client is currently running. In order to mask your online status, the Riot Client needs to be started by Deceive. " +
                "Do you want Deceive to stop the Riot Client and games launched by it, so that it can restart with the proper configuration?",
                DeceiveTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1
            );

            if (result is not DialogResult.Yes)
                return;
            Utils.KillProcesses();
            await Task.Delay(2000); // Riot Client takes a while to die
        }

        try
        {
            File.WriteAllText(Path.Combine(Persistence.DataDir, "debug.log"), string.Empty);
            Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(Persistence.DataDir, "debug.log")));
            Debug.AutoFlush = true;
            Trace.WriteLine(DeceiveTitle);
        }
        catch
        {
            // ignored; just don't save logs if file is already being accessed
        }

        // Step 0: Check for updates in the background.
        _ = Utils.CheckForUpdatesAsync();

        // Step 1: Open a port for our chat proxy, so we can patch chat port into clientconfig.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Trace.WriteLine($"Chat proxy listening on port {port}");

        // Step 2: Find the Riot Client.
        var riotClientPath = Utils.GetRiotClientPath();

        // If the riot client doesn't exist, the user is either severely outdated or has a bugged install.
        if (riotClientPath is null)
        {
            MessageBox.Show(
                "Deceive was unable to find the path to the Riot Client. Usually this can be resolved by launching any Riot Games game once, then launching Deceive again. " +
                "If this does not resolve the issue, please file a bug report through GitHub (https://github.com/molenzwiebel/Deceive) or Discord.",
                DeceiveTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1
            );

            return;
        }

        // If launching "auto", use the persisted launch game (which defaults to prompt).
        if (game is LaunchGame.Auto)
            game = Persistence.GetDefaultLaunchGame();

        // If prompt, display dialog.
        if (game is LaunchGame.Prompt)
        {
            new GamePromptForm().ShowDialog();
            game = GamePromptForm.SelectedGame;
        }

        // If we don't have a concrete game by now, the user has cancelled and nothing we can do.
        if (game is LaunchGame.Prompt or LaunchGame.Auto)
            return;

        var launchProduct = game switch
        {
            LaunchGame.LoL => "league_of_legends",
            LaunchGame.LoR => "bacon",
            LaunchGame.VALORANT => "valorant",
            LaunchGame.Lion => "lion",
            LaunchGame.RiotClient => null,
            var x => throw new Exception("Unexpected LaunchGame: " + x)
        };

        // Step 3: Ensure we have a trusted certificate for the chat proxy.
        var certificate = SetupCertificate();

        // Step 4: Start proxy web server for clientconfig
        var proxyServer = new ConfigProxy(port);

        // Step 5: Launch Riot Client (+game)
        var startArgs = new ProcessStartInfo { FileName = riotClientPath, Arguments = $"--client-config-url=\"http://127.0.0.1:{proxyServer.ConfigPort}\" --insecure" };

        if (launchProduct is not null)
            startArgs.Arguments += $" --launch-product={launchProduct} --launch-patchline={gamePatchline}";

        if (riotClientParams is not null)
            startArgs.Arguments += $" {riotClientParams}";

        if (gameParams is not null)
            startArgs.Arguments += $" -- {gameParams}";

        Trace.WriteLine($"About to launch Riot Client with parameters:\n{startArgs.Arguments}");
        var riotClient = Process.Start(startArgs);
        // Kill Deceive when Riot Client has exited, so no ghost Deceive exists.
        if (riotClient is not null)
        {
            ListenToRiotClientExit(riotClient);
        }

        var mainController = new MainController();

        // Step 6: Get chat server and port for this player by listening to event from ConfigProxy.
        var servingClients = false;
        proxyServer.PatchedChatServer += (_, args) =>
        {
            Trace.WriteLine($"The original chat server details were {args.ChatHost}:{args.ChatPort}");

            // Step 7: Start serving incoming connections and proxy them!
            if (servingClients)
                return;
            servingClients = true;
            if (args.ChatHost is not null)
            {
                mainController.StartServingClients(listener, args.ChatHost, args.ChatPort, certificate);
            }
        };

        // Loop infinitely and handle window messages/tray icon.
        Application.Run(mainController);
    }

    /// Sets up a trusted certificate for the chat proxy. On first run, generates a certificate
    /// with SAN IP:127.0.0.1, installs it in Trusted Root CAs (prompts user), and saves the PFX.
    /// On subsequent runs, loads the existing certificate.
    private static X509Certificate2 SetupCertificate()
    {
        var certPfxPath = Path.Combine(Persistence.DataDir, "proxy-cert.pfx");
        const string certPassword = "deceive";

        // Try loading existing cert
        if (File.Exists(certPfxPath))
        {
            try
            {
                var existing = new X509Certificate2(certPfxPath, certPassword);
                if (existing.NotAfter > DateTime.Now.AddDays(30))
                {
                    Trace.WriteLine("Loaded existing proxy certificate: " + existing.Thumbprint);
                    return existing;
                }

                Trace.WriteLine("Existing certificate is expiring soon, regenerating.");
            }
            catch (Exception e)
            {
                Trace.WriteLine("Failed to load existing certificate, regenerating: " + e.Message);
            }
        }

        Trace.WriteLine("Generating new proxy certificate...");

        // Write PowerShell script to temp file to avoid escaping issues
        var scriptPath = Path.Combine(Persistence.DataDir, "gen-cert.ps1");
        var cerExportPath = Path.Combine(Persistence.DataDir, "proxy-cert.cer");

        File.WriteAllText(scriptPath,
            "$ErrorActionPreference = 'Stop'\n" +
            "$cert = New-SelfSignedCertificate -Subject 'CN=Deceive Proxy' " +
            "-TextExtension @('2.5.29.17={text}IPAddress=127.0.0.1&DNS=127.0.0.1') " +
            "-CertStoreLocation 'Cert:\\CurrentUser\\My' " +
            "-NotAfter (Get-Date).AddYears(10) " +
            "-KeyUsageProperty All " +
            "-KeyUsage DigitalSignature,KeyEncipherment " +
            "-KeyAlgorithm RSA -KeyLength 2048\n" +
            "$pwd = ConvertTo-SecureString -String '" + certPassword + "' -AsPlainText -Force\n" +
            "Export-PfxCertificate -Cert $cert -FilePath '" + certPfxPath.Replace("'", "''") + "' -Password $pwd | Out-Null\n" +
            "Export-Certificate -Cert $cert -FilePath '" + cerExportPath.Replace("'", "''") + "' | Out-Null\n" +
            "Write-Output $cert.Thumbprint\n"
        );

        var genProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        genProcess.Start();
        var output = genProcess.StandardOutput.ReadToEnd().Trim();
        var errors = genProcess.StandardError.ReadToEnd().Trim();
        genProcess.WaitForExit();

        try { File.Delete(scriptPath); } catch { /* ignore */ }

        if (genProcess.ExitCode != 0 || !File.Exists(certPfxPath))
        {
            Trace.WriteLine("Certificate generation failed: " + errors);
            Trace.WriteLine("Falling back to embedded certificate.");
            return new X509Certificate2(Properties.Resources.Certificate);
        }

        Trace.WriteLine("Generated certificate with thumbprint: " + output);

        // Install in Trusted Root CAs so the Riot Client trusts it.
        // certutil -user -addstore will show a Windows security dialog for user consent.
        if (File.Exists(cerExportPath))
        {
            Trace.WriteLine("Installing certificate in Trusted Root CAs...");
            var installProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "certutil.exe",
                    Arguments = "-user -addstore Root \"" + cerExportPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                }
            };

            installProcess.Start();
            installProcess.StandardOutput.ReadToEnd();
            installProcess.WaitForExit();

            if (installProcess.ExitCode == 0)
                Trace.WriteLine("Certificate installed in Trusted Root CAs.");
            else
                Trace.WriteLine("User declined or failed to install certificate in Trusted Root CAs.");

            try { File.Delete(cerExportPath); } catch { /* ignore */ }
        }

        return new X509Certificate2(certPfxPath, certPassword);
    }

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Log all unhandled exceptions
        Trace.WriteLine(e.ExceptionObject as Exception);
        Trace.WriteLine(Environment.StackTrace);
    }

    private static void ListenToRiotClientExit(Process riotClientProcess)
    {
        riotClientProcess.EnableRaisingEvents = true;
        riotClientProcess.Exited += async (sender, e) =>
        {
            Trace.WriteLine("Detected Riot Client exit.");
            await Task.Delay(3000); // wait for a bit to ensure this is not a relaunch triggered by the RC

            var newProcess = Utils.GetRiotClientProcess();
            if (newProcess is not null)
            {
                Trace.WriteLine("A new Riot Client process spawned, monitoring that for exits.");
                ListenToRiotClientExit(newProcess);
            }
            else
            {
                Trace.WriteLine("No new clients spawned after waiting, killing ourselves.");
                Environment.Exit(0);
            }
        };
    }
}
