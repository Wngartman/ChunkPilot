using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ChunkPilot.App;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

public sealed class MessageDialogWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_resources_wrap_literal_text_and_keep_cancel_as_default(bool highContrast)
    {
        WpfDesignSystemHost.Run(() =>
        {
            var window = Create();
            try
            {
                AppTheme.ApplyPreview(window, highContrast, motionEnabled: false);
                window.Measure(new Size(800, 700));
                var cancel = Control<Button>(window, "CancelButton");
                Assert.True(cancel.IsDefault);
                Assert.True(cancel.IsCancel);
                Assert.Same(cancel, FocusManager.GetFocusedElement(window));
                Assert.False(Control<Button>(window, "ConfirmButton").IsDefault);
                Assert.Equal("Restore backup", Control<Button>(window, "ConfirmButton").Content);
                Assert.Equal("Literal <test> & world path", Control<TextBlock>(window, "MessageText").Text);
                Assert.Equal(TextWrapping.Wrap, Control<TextBlock>(window, "MessageText").TextWrapping);
                Assert.Equal(ScrollBarVisibility.Disabled, Control<ScrollViewer>(window, "MessageScroller").HorizontalScrollBarVisibility);
                Assert.Equal(SystemParameters.WorkArea.Height, window.MaxHeight);
                Assert.Equal(Application.Current.FindResource("AppDialogWindow"), window.Style);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("Cancel", false)]
    [InlineData("Escape", false)]
    [InlineData("Close", false)]
    [InlineData("Confirm", true)]
    public void Real_modal_result_requires_explicit_confirmation(string action, bool expected)
    {
        WpfDesignSystemHost.Run(() =>
        {
            var window = Create();
            var result = ShowSynthetic(window, () =>
            {
                if (action == "Close") window.Close(); // Native close/Alt+F4 share this close path.
                else if (action == "Escape")
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                else Control<Button>(window, action == "Cancel" ? "CancelButton" : "ConfirmButton")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.Equal(expected, result == true);
        });
    }

    [Theory]
    [InlineData(AppIconKind.Error)]
    [InlineData(AppIconKind.Info)]
    public void Message_variants_only_offer_close_and_cannot_confirm(AppIconKind icon)
    {
        WpfDesignSystemHost.Run(() =>
        {
            var window = new MessageDialogWindow("Message", "Synthetic message", false, icon);
            Assert.Equal("Close", Control<Button>(window, "CancelButton").Content);
            Assert.Equal(Visibility.Collapsed, Control<Button>(window, "ConfirmButton").Visibility);
            Assert.Equal(icon, Control<AppIcon>(window, "MessageIcon").Kind);
            Assert.False(ShowSynthetic(window, () =>
            {
                // Even a synthetic click on the hidden action must not accept a message.
                Control<Button>(window, "ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Control<Button>(window, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }) == true);
        });
    }

    private static MessageDialogWindow Create() =>
        new("Restore backup", "Literal <test> & world path", true, AppIconKind.Warning);

    [Fact]
    public void Long_message_scrolls_without_hiding_the_confirmation_footer()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var window = new MessageDialogWindow("Restore backup",
                string.Join(Environment.NewLine, Enumerable.Repeat("Synthetic recovery detail.", 200)), true, AppIconKind.Warning);
            ShowSynthetic(window, () =>
            {
                var scroller = Control<ScrollViewer>(window, "MessageScroller");
                var cancel = Control<Button>(window, "CancelButton");
                Assert.True(scroller.ScrollableHeight > 0);
                Assert.True(scroller.ViewportHeight > 0);
                Assert.True(window.ActualHeight <= window.MaxHeight);
                var footerBottom = cancel.TranslatePoint(new Point(0, cancel.ActualHeight), window).Y;
                Assert.True(footerBottom <= window.ActualHeight);
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        });
    }

    private static T Control<T>(FrameworkElement window, string name) => Assert.IsType<T>(window.FindName(name));

    private static bool? ShowSynthetic(MessageDialogWindow window, Action action)
    {
        // Exercise WPF's modal loop without foreground input or a visible product/Agent window.
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Opacity = 0;
        var timedOut = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timedOut = true; timer.Stop(); window.Close(); };
        Exception? failure = null;
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; window.Close(); }
        }));
        timer.Start();
        try
        {
            var result = window.ShowDialog();
            Assert.False(timedOut, "Synthetic modal action did not complete within five seconds.");
            Assert.Null(failure);
            return result;
        }
        finally { timer.Stop(); window.Close(); }
    }
}
