using System.Text.Json;

namespace LocalMessaging.Host;

internal sealed class BoundedLogProvider(string directory) : ILoggerProvider
{
    private readonly object _sync = new();
    private bool _ioFailureReported;
    private const long MaximumBytes = 1024 * 1024;
    public ILogger CreateLogger(string categoryName) => new Logger(this,categoryName);
    public void Dispose() { }
    private void Write<T>(string category, LogLevel level, EventId eventId, T state, Exception? error, Func<T,Exception?,string> format)
    {
        // Parameterized provider logging excludes body payloads; never serialize exceptions/private keys.
        var message = format(state, null);
        if (message.Length > 4096) message = message[..4096];
        var record = JsonSerializer.Serialize(new { atUtc=DateTime.UtcNow, level=level.ToString(), category, eventId=eventId.Id, message, exceptionType=error?.GetType().Name });
        lock (_sync)
        {
            try
            {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory,"host.jsonl");
            if (File.Exists(file) && new FileInfo(file).Length + System.Text.Encoding.UTF8.GetByteCount(record) > MaximumBytes)
            {
                var oldest = file + ".3";
                if (File.Exists(oldest)) File.Delete(oldest);
                for (var i=2;i>=1;i--) if (File.Exists(file+"."+i)) File.Move(file+"."+i,file+"."+(i+1),true);
                File.Move(file,file+".1",true);
            }
            File.AppendAllText(file,record+Environment.NewLine);
            _ioFailureReported=false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must not change the outcome of an already committed command.
                // Keep one visible fallback per failure episode, without an unbounded retry queue.
                if (!_ioFailureReported)
                {
                    _ioFailureReported=true;
                    try { Console.Error.WriteLine("LocalMessaging diagnostic log unavailable ("+e.GetType().Name+"). Application operations continue; check the state-directory volume."); }
                    catch (IOException) { }
                }
            }
        }
    }
    private sealed class Logger(BoundedLogProvider owner,string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {
            if (IsEnabled(level)) owner.Write(category,level,eventId,state,exception,formatter);
        }
    }
}
