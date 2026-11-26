using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FileHub.Desktop.Services;
using FileHub.Desktop.ViewModels;

namespace FileHub.Desktop.Views.Controls;

public partial class CodeEditorPage : UserControl
{
    private readonly CodeEditorViewModel _vm;
    private MonacoAssetServer? _server;

    public CodeEditorPage()
    {
        InitializeComponent();
        _vm = ShellViewModel.Current.CodeEditor;
        DataContext = _vm;

        _vm.FilePayloadReady += OnFilePayloadReady;
        _vm.ThemePayloadReady += OnThemePayloadReady;
        _vm.SnippetsPayloadReady += OnSnippetsPayloadReady;
        _vm.SaveRequested += OnSaveRequested;

        EditorView.WebMessageReceived += OnWebMessage;
        EditorView.NavigationCompleted += OnNavigationCompleted;
        Loaded += OnLoaded;

        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_server is not null) return;
        EngineHint.IsVisible = true;
        _server = new MonacoAssetServer();
        EditorView.Source = new Uri(_server.BaseUrl);
    }

    private void OnNavigationCompleted(object? sender, Avalonia.Controls.WebViewNavigationCompletedEventArgs e)
    {
        EngineHint.IsVisible = false;
        if (e.IsSuccess) _vm.MarkEngineReady();
        else _vm.HandleMessage("{\"type\":\"engine\",\"message\":\"Editor engine failed to load\"}");
    }

    private void OnWebMessage(object? sender, Avalonia.Controls.WebMessageReceivedEventArgs e)
        => _vm.HandleMessage(e.Body ?? string.Empty);

    private void OnFilePayloadReady(string payload)
        => RunScript($"window.fileHub.openFile({JsonSerializer.Serialize(payload)})");

    private void OnThemePayloadReady(string payload)
        => RunScript($"window.fileHub.setTheme({JsonSerializer.Serialize(payload)})");

    private void OnSnippetsPayloadReady(string payload)
        => RunScript($"window.fileHub.registerSnippets({JsonSerializer.Serialize(payload)})");

    private async void OnSaveRequested()
    {
        try
        {
            var json = await EditorView.InvokeScript("window.fileHub.getValue()");
            if (json is not null) await _vm.SaveAsync(json);
            await EditorView.InvokeScript("window.fileHub.markSaved()");
        }
        catch (Exception ex)
        {
            // Never write an empty fallback: a failed read must not destroy the file on disk.
            _vm.HandleMessage(JsonSerializer.Serialize(new { type = "engine", message = ex.Message }));
        }
    }

    private void RunScript(string script)
        => Dispatcher.UIThread.Post(async () =>
        {
            try { await EditorView.InvokeScript(script); }
            catch (Exception ex) { _vm.HandleMessage(JsonSerializer.Serialize(new { type = "engine", message = ex.Message })); }
        });

    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider.CanOpen != true) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = Loc.T("CodeOpenFile")
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await _vm.OpenAsync(path);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        if (_vm.SaveCommand.CanExecute(null)) _vm.SaveCommand.Execute(null);
    }
}
