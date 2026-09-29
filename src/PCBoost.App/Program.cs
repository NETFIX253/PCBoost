using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace PCBoost.App;

/// <summary>
/// Point d'entrée : instance unique (une deuxième ouverture ramène la fenêtre existante), puis démarrage WinUI.
/// Argument « --background » : démarrage discret dans la zone de notification (lancement avec Windows).
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "PCBoost.Main";

    public static bool StartInBackground { get; private set; }

    /// <summary>« --page &lt;clé&gt; » : page ouverte au démarrage (lien direct, tests d'interface).</summary>
    public static string? StartPage { get; private set; }

    /// <summary>« --theme light|dark|system » : thème pour cette session uniquement (non enregistré).</summary>
    public static string? ThemeOverride { get; private set; }

    /// <summary>« --lang fr|en » : langue pour cette session uniquement (non enregistrée).</summary>
    public static string? LanguageOverride { get; private set; }

    /// <summary>« --capture-dir &lt;dossier&gt; --capture-pages a,b,c » : captures d'écran automatiques des pages (tests d'interface), puis fermeture.</summary>
    public static string? CaptureDirectory { get; private set; }

    public static IReadOnlyList<string> CapturePages { get; private set; } = [];

    /// <summary>Déclenché dans l'instance principale quand une autre instance est lancée.</summary>
    public static event EventHandler? SecondInstanceActivated;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartInBackground = args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        StartPage = ValueAfter(args, "--page");
        ThemeOverride = ValueAfter(args, "--theme");
        LanguageOverride = ValueAfter(args, "--lang");
        CaptureDirectory = ValueAfter(args, "--capture-dir");
        CapturePages = (ValueAfter(args, "--capture-pages") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (CaptureDirectory is not null) StartInBackground = false;

        if (CaptureDirectory is null && RedirectToExistingInstance())
            return 0;

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    private static string? ValueAfter(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool RedirectToExistingInstance()
    {
        AppInstance mainInstance;
        try
        {
            mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        }
        catch (COMException)
        {
            return false;
        }

        if (mainInstance.IsCurrent)
        {
            mainInstance.Activated += (_, _) => SecondInstanceActivated?.Invoke(null, EventArgs.Empty);
            return false;
        }

        // Redirection sans bloquer le thread STA (modèle documenté du Windows App SDK).
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var redirectEvent = CreateEventW(IntPtr.Zero, true, false, null);
        _ = Task.Run(() =>
        {
            try
            {
                mainInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                // L'instance principale est en cours d'arrêt : rien à faire.
            }
            finally
            {
                SetEvent(redirectEvent);
            }
        });
        const uint CWMO_DEFAULT = 0;
        const uint INFINITE = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, [redirectEvent], out _);
        CloseHandle(redirectEvent);
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
}
