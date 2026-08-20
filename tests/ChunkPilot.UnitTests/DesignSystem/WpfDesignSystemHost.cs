using System.Windows;
using System.Windows.Threading;
using ChunkPilot.App.DesignSystem;

namespace ChunkPilot.UnitTests.DesignSystem;

/// <summary>
/// A single STA thread with a WPF <see cref="Application"/> and the real design system loaded.
/// </summary>
/// <remarks>
/// <para>
/// This is what lifts the design-system tests above text matching. Loading the actual dictionaries
/// through <see cref="AppTheme.Initialize"/> resolves every <c>StaticResource</c> and every
/// <c>Style.BasedOn</c> exactly as the running application does, so a missing key or a broken
/// template fails a unit test instead of a user's window.
/// </para>
/// <para>
/// One host is shared by all tests and lives for the process. Every WPF touch is marshalled onto its
/// dispatcher, so xUnit's parallelism cannot put two threads inside WPF at once.
/// </para>
/// </remarks>
internal static class WpfDesignSystemHost
{
    private static readonly object Gate = new();
    private static Dispatcher? dispatcher;
    private static Exception? startupFailure;

    /// <summary>Runs <paramref name="callback"/> on the host's STA thread and returns its result.</summary>
    public static T Run<T>(Func<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return EnsureStarted().Invoke(callback);
    }

    /// <summary>Runs <paramref name="callback"/> on the host's STA thread.</summary>
    public static void Run(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnsureStarted().Invoke(callback);
    }

    private static Dispatcher EnsureStarted()
    {
        lock (Gate)
        {
            if (startupFailure is not null)
                throw new InvalidOperationException("The WPF design-system test host failed to start.", startupFailure);
            if (dispatcher is not null)
                return dispatcher;

            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    var application = Application.Current ?? new Application();
                    application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    AppTheme.Initialize(application);
                    dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception exception)
                {
                    startupFailure = exception;
                }
                finally
                {
                    ready.Set();
                }

                if (startupFailure is null)
                    Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "ChunkPilot design-system test host"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(60)))
                throw new TimeoutException("The WPF design-system test host did not start within 60 seconds.");
            if (startupFailure is not null)
                throw new InvalidOperationException("The WPF design-system test host failed to start.", startupFailure);
            return dispatcher!;
        }
    }

    /// <summary>
    /// Every resource key reachable from the loaded application, with the dictionary that declared it.
    /// </summary>
    /// <remarks>
    /// Reading each value forces WPF to realise deferred content, which is precisely how a broken
    /// cross-dictionary reference is caught.
    /// </remarks>
    public static IReadOnlyList<(string Source, object Key, object? Value)> EnumerateResources() =>
        Run(() =>
        {
            var found = new List<(string, object, object?)>();
            Collect(Application.Current.Resources, found);
            return (IReadOnlyList<(string, object, object?)>)found;
        });

    /// <summary>Resolves a single resource by key on the host thread.</summary>
    public static object? Resolve(string key) => Run(() => Application.Current.TryFindResource(key));

    /// <summary>Loads a dictionary that is not part of the default theme, such as an overlay.</summary>
    public static IReadOnlyList<string> LoadDictionaryKeys(Uri source) =>
        Run(() =>
        {
            var dictionary = new ResourceDictionary { Source = source };
            return (IReadOnlyList<string>)dictionary.Keys.OfType<object>()
                .Select(key => key.ToString() ?? string.Empty)
                .ToArray();
        });

    private static void Collect(ResourceDictionary dictionary, List<(string, object, object?)> found)
    {
        var source = dictionary.Source?.ToString() ?? "(inline)";
        foreach (var key in dictionary.Keys.OfType<object>().ToArray())
            found.Add((source, key, dictionary[key]));
        foreach (var merged in dictionary.MergedDictionaries)
            Collect(merged, found);
    }
}
