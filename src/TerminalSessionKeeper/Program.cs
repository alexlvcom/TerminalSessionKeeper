using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Native;
using TerminalSessionKeeper.Restore;
using TerminalSessionKeeper.Services;
using TerminalSessionKeeper.Settings;
using TerminalSessionKeeper.Snapshots;
using TerminalSessionKeeper.Terminal;
using TerminalSessionKeeper.Ui;

namespace TerminalSessionKeeper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Typing into a restored tab runs as a short-lived copy of this executable: attaching to
        // a console is process-wide, and whoever is attached receives that console's close
        // event, which must never be the tray app. Handled before anything else starts up.
        if (args.Length > 0 && args[0] == ConsoleInjector.HelperArgument)
        {
            return ConsoleInjector.RunHelper(args);
        }

        using var instance = new SingleInstance();

        if (!instance.IsFirstInstance)
        {
            // Another copy already owns the tray icon. Ask it to show itself and exit quietly
            // rather than adding a second icon — and, more to the point, rather than letting two
            // timers snapshot and restore over each other.
            instance.SignalExistingInstance();
            return 0;
        }

        AppPaths.EnsureCreated();

        using var log = new FileLog(AppPaths.LogDirectory);
        log.Info($"{AppInfo.DisplayName} v{Changelog.MarketingVersion} starting (pid {Environment.ProcessId}).");

        ApplicationConfiguration.Initialize();

        var settingsStore = new SettingsStore(log);
        var snapshotStore = new SnapshotStore(log);

        var overrideStore = new OverrideStore(log);
        overrideStore.EnsureExists();

        var snapshotService = new SnapshotService(
            log,
            snapshotStore,
            overrideStore,
            new WslProbe(log),
            new WtProfiles(log),
            new TerminalWindows(log),
            new TabColorSampler(log),
            new TabColorMemory(log));

        var restoreService = new RestoreService(log);

        // A restore lends Windows Terminal a setTabColor action for a moment and takes it back
        // out. If the app was killed in that moment, the action is still in the user's
        // settings.json; this is where it goes.
        new TabColorApplier(log).RevertLeftovers();
        var startupManager = new StartupManager(log);

        Application.ThreadException += (_, e) => log.Error("Unhandled UI exception.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("Unhandled exception.", e.ExceptionObject as Exception);

        using var context = new TrayApplicationContext(
            snapshotService, snapshotStore, restoreService, startupManager, settingsStore, log);

        instance.StartListening(context.ActivateFromOtherInstance);

        try
        {
            Application.Run(context);
        }
        catch (Exception ex)
        {
            log.Error($"{AppInfo.DisplayName} terminated unexpectedly.", ex);
            return 1;
        }

        log.Info($"{AppInfo.DisplayName} stopped.");
        return 0;
    }
}
