using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HammerTime.Mcp.Shared;

namespace HammerTime.Mcp.Plugin
{
    internal sealed class McpNamedPipeServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly Func<BridgeRequest, Task<BridgeResponse>> _handler;
        private readonly CancellationTokenSource _cancellation;
        private Task _acceptLoop;

        public McpNamedPipeServer(string pipeName, Func<BridgeRequest, Task<BridgeResponse>> handler)
        {
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _cancellation = new CancellationTokenSource();
        }

        public void Start()
        {
            _acceptLoop = Task.Run(() => AcceptLoop(_cancellation.Token));
        }

        public async Task Stop()
        {
            _cancellation.Cancel();
            if (_acceptLoop == null) return;

            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task AcceptLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var stream = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    _ = Task.Run(() => HandleConnection(stream, cancellationToken), cancellationToken);
                }
                catch
                {
                    stream.Dispose();
                    if (cancellationToken.IsCancellationRequested) throw;
                }
            }
        }

        private async Task HandleConnection(Stream stream, CancellationToken cancellationToken)
        {
            using (stream)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await ReadLine(stream, cancellationToken).ConfigureAwait(false);
                    if (line == null) break;

                    BridgeResponse response;
                    try
                    {
                        var request = BridgeJson.DeserializeRequest(line);
                        response = await _handler(request).ConfigureAwait(false);
                    }
                    catch (BridgeProtocolException ex)
                    {
                        response = BridgeResponse.Fail(null, ErrorCodes.InvalidRequest, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        response = BridgeResponse.Fail(null, ErrorCodes.EditorUnavailable, ex.Message);
                    }

                    await WriteLine(stream, BridgeJson.SerializeResponse(response), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static async Task<string> ReadLine(Stream stream, CancellationToken cancellationToken)
        {
            using (var buffer = new MemoryStream())
            {
                var single = new byte[1];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(single, 0, 1, cancellationToken).ConfigureAwait(false);
                    if (read == 0) return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
                    if (single[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF').TrimEnd('\r');
                    buffer.WriteByte(single[0]);
                }
            }

            return null;
        }

        private static async Task WriteLine(Stream stream, string line, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
    }
}
