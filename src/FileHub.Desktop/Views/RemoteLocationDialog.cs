// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// "Add network location" dialog. Kept in code so the field set can grow with each protocol
// without a XAML round trip; the protocol choice drives which rows are relevant.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using FileHub.Files.Vfs;

namespace FileHub.Desktop.Views;

public sealed class RemoteLocationDialog : Window
{
    private readonly TaskCompletionSource<RemoteLocation?> _result = new();

    private readonly ComboBox _protocol = new()
    {
        ItemsSource = new[] { "SMB (Windows share / NAS)", "SFTP (SSH)", "WebDAV", "S3 compatible" },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private readonly TextBox _name = new() { PlaceholderText = "Display name, e.g. Home NAS" };
    private readonly TextBox _host = new() { PlaceholderText = "host or IP, e.g. 192.168.1.10" };
    private readonly TextBox _port = new() { PlaceholderText = "leave empty for the default port" };
    private readonly TextBox _user = new() { PlaceholderText = "user name" };
    private readonly TextBox _password = new() { PasswordChar = '*', PlaceholderText = "password or access key" };
    private readonly TextBox _path = new() { PlaceholderText = "share / remote path, e.g. share/media or bucket/prefix" };
    private readonly TextBox _region = new() { PlaceholderText = "S3 region (default us-east-1)", IsVisible = false };
    private readonly TextBox _endpoint = new() { PlaceholderText = "S3 endpoint, e.g. http://minio.local:9000", IsVisible = false };
    private readonly TextBox _secret = new() { PasswordChar = '*', PlaceholderText = "S3 secret key", IsVisible = false };
    private readonly CheckBox _tls = new() { Content = "Use TLS (https / implicit FTPS)", IsChecked = true };
    private readonly TextBlock _error = new() { FontSize = 12, Foreground = Avalonia.Media.Brushes.OrangeRed, IsVisible = false };

    private RemoteLocationDialog()
    {
        Title = "Add network location";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var form = new StackPanel { Margin = new Thickness(20), Spacing = 8 };

        void Add(string label, Control control)
        {
            form.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) });
            form.Children.Add(control);
        }

        Add("Protocol", _protocol);
        Add("Name", _name);
        Add("Host", _host);
        Add("Port", _port);
        Add("User", _user);
        Add("Password", _password);
        Add("Path", _path);
        form.Children.Add(_tls);
        form.Children.Add(_region);
        form.Children.Add(_endpoint);
        form.Children.Add(_secret);
        form.Children.Add(_error);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        cancel.Click += (_, _) => { _result.TrySetResult(null); Close(); };
        var connect = new Button { Content = "Connect", MinWidth = 96, Classes = { "accent" } };
        connect.Click += (_, _) => Confirm();
        footer.Children.Add(cancel);
        footer.Children.Add(connect);
        form.Children.Add(footer);

        Content = form;
        Closed += (_, _) => _result.TrySetResult(null);
        _protocol.SelectionChanged += (_, _) => SyncProtocolFields();
        SyncProtocolFields();
    }

    public static async Task<RemoteLocation?> ShowAsync()
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (owner is null) return null;
        var dialog = new RemoteLocationDialog();
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return await dialog._result.Task.ConfigureAwait(true);
    }

    private RemoteProtocol SelectedProtocol => _protocol.SelectedIndex switch
    {
        1 => RemoteProtocol.Sftp,
        2 => RemoteProtocol.WebDav,
        3 => RemoteProtocol.S3,
        _ => RemoteProtocol.Smb
    };

    private void SyncProtocolFields()
    {
        var s3 = SelectedProtocol == RemoteProtocol.S3;
        _region.IsVisible = s3;
        _endpoint.IsVisible = s3;
        _secret.IsVisible = s3;
        _tls.IsVisible = SelectedProtocol is RemoteProtocol.WebDav or RemoteProtocol.Smb;
        _port.PlaceholderText = $"default {DefaultPortFor(SelectedProtocol)}";
        _host.PlaceholderText = s3 ? "bucket name" : "host or IP, e.g. 192.168.1.10";
        _path.PlaceholderText = s3 ? "key prefix (optional)" : "share / remote path, e.g. share/media";
    }

    private static int DefaultPortFor(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => 22,
        RemoteProtocol.WebDav => 443,
        RemoteProtocol.S3 => 443,
        _ => 445
    };

    private void Confirm()
    {
        var host = (_host.Text ?? string.Empty).Trim();
        if (host.Length == 0)
        {
            Fail("Host is required.");
            return;
        }

        var location = new RemoteLocation
        {
            Protocol = SelectedProtocol,
            Name = string.IsNullOrWhiteSpace(_name.Text) ? host : _name.Text!.Trim(),
            Host = host,
            Port = int.TryParse(_port.Text, out var port) ? port : 0,
            User = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text!.Trim(),
            Password = string.IsNullOrWhiteSpace(_password.Text) ? null : _password.Text,
            BasePath = string.IsNullOrWhiteSpace(_path.Text) ? null : _path.Text!.Trim().Trim('/'),
            UseTls = _tls.IsChecked == true,
            Region = string.IsNullOrWhiteSpace(_region.Text) ? null : _region.Text!.Trim(),
            Endpoint = string.IsNullOrWhiteSpace(_endpoint.Text) ? null : _endpoint.Text!.Trim(),
            SecretKey = string.IsNullOrWhiteSpace(_secret.Text) ? null : _secret.Text
        };

        if (location.Protocol == RemoteProtocol.Smb && string.IsNullOrEmpty(location.BasePath))
        {
            Fail("SMB needs a share name in Path, for example \"media\".");
            return;
        }
        if (location.Protocol == RemoteProtocol.Sftp && string.IsNullOrEmpty(location.User))
        {
            Fail("SFTP needs a user name.");
            return;
        }

        _result.TrySetResult(location);
        Close();
    }

    private void Fail(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }
}
