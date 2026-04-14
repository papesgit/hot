using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace HlaeObsTools.Services.Logging;

internal static class ConsoleLogFile
{
    private static TextWriter? _writer;
    private static string? _logPath;
    private static readonly object SyncRoot = new();

    public static void Install()
    {
        if (_writer != null)
        {
            return;
        }

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var file = CreateLogWriter();
        if (file == null)
        {
            return;
        }

        _writer = file;
        var output = new TeeTextWriter(originalOut, file, SyncRoot);
        var error = new TeeTextWriter(originalError, file, SyncRoot);

        Console.SetOut(output);
        Console.SetError(error);
        Console.WriteLine($"[ConsoleLogFile] Log file: {_logPath}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"[UnhandledException] {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Console.Error.WriteLine($"[UnobservedTaskException] {e.Exception}");
    }

    private static StreamWriter? CreateLogWriter()
    {
        try
        {
            var path = GetLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _logPath = path;
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
        }
        catch
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "hlae_obs_tools.log");
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _logPath = path;
                return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
            }
            catch
            {
                return null;
            }
        }
    }

    private static string GetLogPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "hlae_obs_tools.log");
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _file;
        private readonly object _syncRoot;

        public TeeTextWriter(TextWriter console, TextWriter file, object syncRoot)
        {
            _console = console;
            _file = file;
            _syncRoot = syncRoot;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            lock (_syncRoot)
            {
                _console.Write(value);
                _file.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_syncRoot)
            {
                _console.Write(value);
                _file.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_syncRoot)
            {
                _console.WriteLine(value);
                _file.WriteLine(value);
            }
        }

        public override void Flush()
        {
            lock (_syncRoot)
            {
                _console.Flush();
                _file.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
