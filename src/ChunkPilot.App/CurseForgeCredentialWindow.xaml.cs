using System.ComponentModel;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App;

/// <summary>Native credential input. No credential is exposed to WebView2 or a ViewModel.</summary>
public partial class CurseForgeCredentialWindow : Window
{
    private readonly Func<string, CancellationToken, Task<OperationResult>> save;
    private readonly Func<CancellationToken, Task<OperationResult>> remove;
    private readonly Action openAccessGuide;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private bool closed;
    public bool Configured { get; private set; }

    public CurseForgeCredentialWindow(bool configured,
        Func<string, CancellationToken, Task<OperationResult>> save,
        Func<CancellationToken, Task<OperationResult>> remove,
        Action openAccessGuide)
    {
        this.save = save;
        this.remove = remove;
        this.openAccessGuide = openAccessGuide;
        Configured = configured;
        InitializeComponent();
        AppWindowChrome.Apply(this);
        CurrentStatus.Text = configured
            ? "A key is saved. A replacement is saved only after successful validation."
            : "No personal key is saved. You can also use Modrinth or import a local server pack.";
        RemoveButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => KeyBox.Focus();
        Closing += OnClosing;
        Closed += (_, _) => lifetime.Dispose();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        closed = true;
        KeyBox.Clear();
        lifetime.Cancel();
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (SaveButton is null) return;
        using var password = KeyBox.SecurePassword;
        SaveButton.IsEnabled = !busy && password.Length > 0;
    }

    private void AccessGuide_Click(object sender, RoutedEventArgs e)
    {
        try { openAccessGuide(); }
        catch (Exception)
        {
            ValidationAlert.Message = "The browser could not open. Visit CurseForge Support and search for API access.";
            ValidationAlert.Visibility = Visibility.Visible;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !SaveButton.IsEnabled) return;
        var password = KeyBox.Password;
        KeyBox.Clear();
        // The save delegate protects the value synchronously before returning its network task.
        // Drop this reference before awaiting validation; immutable managed strings cannot be zeroed.
        var pending = ChangeAsync(token => save(password, token), configured: true);
        password = string.Empty;
        await pending.ConfigureAwait(true);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        KeyBox.Clear();
        await ChangeAsync(remove, configured: false).ConfigureAwait(true);
    }

    private async Task ChangeAsync(Func<CancellationToken, Task<OperationResult>> change, bool configured)
    {
        busy = true;
        KeyBox.IsEnabled = false;
        SaveButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        ValidationAlert.Visibility = Visibility.Collapsed;
        ProgressText.Text = configured ? "Checking with CurseForge…" : "Removing saved access…";
        ProgressText.Visibility = Visibility.Visible;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var result = await change(timeout.Token).ConfigureAwait(true);
            if (closed) return;
            if (result.Success)
            {
                Configured = configured;
                DialogResult = true;
                return;
            }
            ValidationAlert.Message = "CurseForge access could not be updated. Check your approved key and connection, then try again. Any previously saved key is preserved.";
            ValidationAlert.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            if (closed) return;
            ValidationAlert.Message = "The request timed out. Check the connection and try again.";
            ValidationAlert.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            if (closed) return;
            // Provider/transport exceptions may contain request details. Keep them out of this UI.
            ValidationAlert.Message = "The native service could not update CurseForge access. Try again when ChunkPilot is connected.";
            ValidationAlert.Visibility = Visibility.Visible;
        }
        finally
        {
            busy = false;
            if (!closed)
            {
                KeyBox.IsEnabled = true;
                RemoveButton.IsEnabled = true;
                ProgressText.Visibility = Visibility.Collapsed;
                KeyBox.Focus();
            }
        }
    }
}
