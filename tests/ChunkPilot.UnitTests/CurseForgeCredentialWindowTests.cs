using System.Windows;
using System.Windows.Controls;
using ChunkPilot.App;
using ChunkPilot.Core;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeCredentialWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_dialog_loads_shared_resources_and_hides_removal_without_a_saved_key(bool configured)
    {
        WpfDesignSystemHost.Run(() =>
        {
            var guidesOpened = 0;
            var window = new CurseForgeCredentialWindow(configured,
                (_, _) => throw new InvalidOperationException("No save expected."),
                _ => throw new InvalidOperationException("No removal expected."),
                () => guidesOpened++);
            try
            {
                window.Measure(new Size(800, 800));
                var password = Assert.IsType<PasswordBox>(window.FindName("KeyBox"));
                var save = Assert.IsType<Button>(window.FindName("SaveButton"));
                var remove = Assert.IsType<Button>(window.FindName("RemoveButton"));
                Assert.Equal(4096, password.MaxLength);
                Assert.False(save.IsEnabled);
                Assert.Equal(configured ? Visibility.Visible : Visibility.Collapsed, remove.Visibility);
                Assert.IsType<Button>(window.FindName("AccessGuideButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, guidesOpened);
                password.Password = "synthetic-test-input";
                Assert.True(save.IsEnabled);
                window.Close();
                using var cleared = password.SecurePassword;
                Assert.Equal(0, cleared.Length);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Closing_during_validation_cancels_the_native_request_and_clears_input()
    {
        WpfDesignSystemHost.Run(() =>
        {
            CancellationToken observed = default;
            var window = new CurseForgeCredentialWindow(true,
                async (_, token) =>
                {
                    observed = token;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return OperationResult.Ok("Unreachable.");
                },
                _ => throw new InvalidOperationException("No removal expected."), () => { });
            try
            {
                var password = Assert.IsType<PasswordBox>(window.FindName("KeyBox"));
                password.Password = "synthetic-test-input";
                var save = Assert.IsType<Button>(window.FindName("SaveButton"));
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(save.IsEnabled);
                Assert.False(password.IsEnabled);
                Assert.True(observed.CanBeCanceled);
                window.Close();
                Assert.True(observed.IsCancellationRequested);
                Assert.True(window.Configured);
                using var cleared = password.SecurePassword;
                Assert.Equal(0, cleared.Length);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Failed_validation_clears_input_preserves_configuration_and_does_not_echo_provider_errors()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var calls = 0;
            string? received = null;
            var window = new CurseForgeCredentialWindow(true,
                (value, _) =>
                {
                    calls++;
                    received = value;
                    return Task.FromResult(new OperationResult { Success = false, Message = "sensitive-provider-echo" });
                },
                _ => throw new InvalidOperationException("No removal expected."), () => { });
            try
            {
                var password = Assert.IsType<PasswordBox>(window.FindName("KeyBox"));
                password.Password = "synthetic-test-input";
                Assert.IsType<Button>(window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, calls);
                Assert.Equal("synthetic-test-input", received);
                using var cleared = password.SecurePassword;
                Assert.Equal(0, cleared.Length);
                Assert.True(window.Configured);
                var alert = Assert.IsType<ChunkPilot.App.DesignSystem.Components.AppAlert>(window.FindName("ValidationAlert"));
                Assert.Equal(Visibility.Visible, alert.Visibility);
                Assert.DoesNotContain("sensitive-provider-echo", alert.Message, StringComparison.Ordinal);
            }
            finally { window.Close(); }
        });
    }
}
