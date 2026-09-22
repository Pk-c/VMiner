using System.Net.Mail;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VMiner.Services;

namespace VMiner.Windows;

public partial class AccountWindow : Window
{
    private readonly SupabaseAuthService _auth;
    private readonly CancellationTokenSource _cancellation = new();

    internal AccountWindow(SupabaseAuthService auth)
    {
        _auth = auth;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.EnableDarkTitleBar(
            new WindowInteropHelper(this).Handle);
        Closed += (_, _) => _cancellation.Cancel();
        Loaded += (_, _) => EmailBox.Focus();
    }

    private async void SignInClicked(object sender, RoutedEventArgs e)
    {
        if (!TryGetCredentials(out var email, out var password))
            return;

        SetBusy(true, "Signing in…");
        try
        {
            await _auth.SignInAsync(email, password, _cancellation.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
        finally
        {
            if (IsVisible)
                SetBusy(false);
        }
    }

    private async void CreateAccountClicked(object sender, RoutedEventArgs e)
    {
        if (!TryGetCredentials(out var email, out var password))
            return;

        SetBusy(true, "Creating account…");
        try
        {
            var result = await _auth.SignUpAsync(email, password, _cancellation.Token);
            if (result.SignedIn)
            {
                DialogResult = true;
                return;
            }

            StatusText.Foreground = (Brush)FindResource("SuccessBrush");
            StatusText.Text = result.Message;
            PasswordBox.Clear();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
        finally
        {
            if (IsVisible)
                SetBusy(false);
        }
    }

    private bool TryGetCredentials(out string email, out string password)
    {
        email = EmailBox.Text.Trim();
        password = PasswordBox.Password;
        try
        {
            _ = new MailAddress(email);
        }
        catch
        {
            SetError("Enter a valid email address.");
            return false;
        }

        if (password.Length < 6)
        {
            SetError("The password must contain at least 6 characters.");
            return false;
        }

        return true;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        EmailBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy;
        CreateAccountButton.IsEnabled = !busy;
        if (message is not null)
        {
            StatusText.Foreground = (Brush)FindResource("AccentBrush");
            StatusText.Text = message;
        }
    }

    private void SetError(string message)
    {
        StatusText.Foreground = (Brush)FindResource("DangerBrush");
        StatusText.Text = message;
        SetBusy(false);
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
}
