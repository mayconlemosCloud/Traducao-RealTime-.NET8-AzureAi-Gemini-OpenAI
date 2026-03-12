using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MeetingTranslator.ViewModels;

public partial class MainViewModel
{
    // Escritor de log em background
    private static readonly string _logBasePath = AppDomain.CurrentDomain.BaseDirectory;
    private readonly Channel<(string FileName, string Line)> _logChannel =
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private Task? _logWriterTask;
    private readonly CancellationTokenSource _logCts = new();

    private async Task RunLogWriter()
    {
        var writers = new Dictionary<string, StreamWriter>();
        try
        {
            await foreach (var (fileName, line) in _logChannel.Reader.ReadAllAsync(_logCts.Token).ConfigureAwait(false))
            {
                var fullPath = Path.Combine(_logBasePath, fileName);
                if (!writers.TryGetValue(fileName, out var writer))
                {
                    writer = new StreamWriter(fullPath, append: true) { AutoFlush = false };
                    writers[fileName] = writer;
                }
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                Console.WriteLine(line);

                // Flush quando o channel estiver vazio (batch completo)
                if (!_logChannel.Reader.TryPeek(out _))
                {
                    foreach (var w in writers.Values)
                        await w.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* best-effort logging */ }
        finally
        {
            foreach (var w in writers.Values)
            {
                try { await w.FlushAsync(); w.Dispose(); } catch { }
            }
        }
    }
}
