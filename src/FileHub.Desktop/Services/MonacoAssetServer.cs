using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FileHub.Desktop.Services;

/// <summary>
/// Serves the bundled monaco-editor assets from a loopback-only HTTP endpoint. A tiny raw TCP
/// server is used instead of HttpListener because Windows HTTP.sys needs a URL ACL for the latter,
/// which a normal user installation cannot create. Monaco's AMD workers then load same-origin.
/// </summary>
public sealed class MonacoAssetServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _assetRoot;
    private readonly Task _loop;
    private bool _disposed;

    public MonacoAssetServer()
    {
        _assetRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Monaco");
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl { get; }

    public bool IsRunning => !_disposed;

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (string.IsNullOrWhiteSpace(request)) return;

                var firstLine = request.Split('\n')[0].Trim();
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase)) return;

                var rawPath = parts[1].Split('?')[0];
                var path = Uri.UnescapeDataString(rawPath);
                if (!path.StartsWith('/')) { await WriteStatusAsync(stream, 400); return; }

                byte[] body;
                string contentType;
                if (path is "/" or "/index.html")
                {
                    body = Encoding.UTF8.GetBytes(EditorHtml);
                    contentType = "text/html; charset=utf-8";
                }
                else
                {
                    var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    var full = Path.GetFullPath(Path.Combine(_assetRoot, relative));
                    var root = Path.GetFullPath(_assetRoot) + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full))
                    {
                        await WriteStatusAsync(stream, 404);
                        return;
                    }
                    body = await File.ReadAllBytesAsync(full);
                    contentType = MimeOf(full);
                }

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                    "Cache-Control: public, max-age=3600\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or ArgumentException)
        {
            // A closed browser tab or a cancelled app shutdown is normal here.
        }
    }

    private static async Task WriteStatusAsync(NetworkStream stream, int code)
    {
        var reason = code == 404 ? "Not Found" : "Bad Request";
        var bytes = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (sb.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n")) break;
        }
        return sb.ToString();
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".html" => "text/html; charset=utf-8",
        ".ttf" => "font/ttf",
        ".svg" => "image/svg+xml",
        ".map" => "application/json; charset=utf-8",
        _ => "application/octet-stream"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }

    /// <summary>
    /// The page itself is intentionally generic. All file content, language, theme and snippets
    /// are pushed in from C# after navigation so switching files never reloads Monaco.
    /// </summary>
    private const string EditorHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>FileHub Code</title>
<link rel="stylesheet" href="/vs/editor/editor.main.css" />
<style>
  html, body { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #fff; }
  #container { width: 100%; height: 100%; }
  #fallback { display: none; padding: 16px; font: 14px sans-serif; color: #b00; }
</style>
</head>
<body>
<div id="container"></div>
<div id="fallback">Monaco editor failed to load.</div>
<script src="/vs/loader.js"></script>
<script>
(function () {
  function send(payload) {
    if (typeof invokeCSharpAction === 'function') invokeCSharpAction(JSON.stringify(payload));
  }
  window.MonacoEnvironment = {
    getWorkerUrl: function () { return '/vs/base/worker/workerMain.js'; }
  };
  require.config({ paths: { vs: '/vs' } });
  require(['vs/editor/editor.main'], function (monaco) {
    try {
      var editor = monaco.editor.create(document.getElementById('container'), {
        value: '', language: 'plaintext', automaticLayout: true,
        fontFamily: 'Menlo, Consolas, monospace', fontSize: 13,
        minimap: { enabled: true }, scrollBeyondLastLine: false,
        renderLineHighlight: 'line', roundedSelection: false
      });
      window.fileHub = {
        ready: true,
        dirty: false,
        options: { tabSize: 4, insertSpaces: true },
        customLanguages: {},
        openFile: function (json) {
          var cfg = JSON.parse(json);
          if (cfg.language && !window.fileHub.customLanguages[cfg.language]) {
            var known = monaco.languages.getLanguages().some(function (l) { return l.id === cfg.language; });
            if (!known) monaco.languages.register({ id: cfg.language });
            window.fileHub.customLanguages[cfg.language] = true;
          }
          var old = monaco.editor.getModel(monaco.Uri.parse(cfg.uri));
          if (old) old.dispose();
          var model = monaco.editor.createModel(cfg.value || '', cfg.language || 'plaintext', monaco.Uri.parse(cfg.uri));
          model.updateOptions(window.fileHub.options);
          editor.setModel(model);
          window.fileHub.dirty = false;
          model.onDidChangeContent(function () {
            if (!window.fileHub.dirty) {
              window.fileHub.dirty = true;
              send({ type: 'dirty', dirty: true });
            }
          });
          editor.onDidChangeCursorPosition(function (e) {
            send({ type: 'cursor', line: e.position.lineNumber, column: e.position.column });
          });
          editor.focus();
          send({ type: 'ready', language: cfg.language || 'plaintext' });
        },
        getValue: function () { return JSON.stringify(editor.getValue()); },
        markSaved: function () {
          window.fileHub.dirty = false;
          send({ type: 'dirty', dirty: false });
        },
        setLanguage: function (language) {
          monaco.editor.setModelLanguage(editor.getModel(), language || 'plaintext');
          send({ type: 'language', language: language });
        },
        setTheme: function (json) {
          var theme = JSON.parse(json);
          monaco.editor.defineTheme('filehub-extension', theme);
          monaco.editor.setTheme('filehub-extension');
        },
        useBuiltInTheme: function (kind) { monaco.editor.setTheme(kind); },
        registerSnippets: function (json) {
          var groups = JSON.parse(json);
          groups.forEach(function (group) {
            monaco.languages.registerCompletionItemProvider(group.language, {
              provideCompletionItems: function (model, position) {
                var word = model.getWordUntilPosition(position);
                var range = {
                  startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
                  startColumn: word.startColumn, endColumn: word.endColumn
                };
                return { suggestions: group.snippets.map(function (s) {
                  return {
                    label: s.prefix, kind: monaco.languages.CompletionItemKind.Snippet,
                    insertText: Array.isArray(s.body) ? s.body.join('\n') : s.body,
                    insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                    documentation: s.description || '', range: range
                  };
                }) };
              }
            });
          });
        },
        // Settings dialog -> C# -> here. Tab size and indent are model options in Monaco, the
        // rest are editor options; both sides are updated so existing models follow too.
        applyOptions: function (json) {
          try {
            var next = JSON.parse(json);
            window.fileHub.options = {
              tabSize: next.tabSize || 4,
              insertSpaces: next.insertSpaces !== false
            };
            var model = editor.getModel();
            if (model) model.updateOptions(window.fileHub.options);
            var editorOptions = JSON.parse(json);
            delete editorOptions.tabSize;
            delete editorOptions.insertSpaces;
            editor.updateOptions(editorOptions);
            editor.layout();
          } catch (err) {
            send({ type: 'engine', message: 'Options: ' + err });
          }
        },
        layout: function () { editor.layout(); },
        focus: function () { editor.focus(); }
      };
      send({ type: 'engine', state: 'ready' });
    } catch (err) {
      document.getElementById('fallback').style.display = 'block';
      document.getElementById('fallback').textContent = String(err);
      send({ type: 'engine', state: 'error', message: String(err) });
    }
  });
})();
</script>
</body>
</html>
""";
}
