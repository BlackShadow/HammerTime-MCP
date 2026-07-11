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
                // The connection is reused for many lines, so the reader must retain any
                // bytes buffered past a newline for the next ReadLine call.
                var reader = new PipeLineReader(stream);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLine(cancellationToken).ConfigureAwait(false);
                    if (line == null) break;

                    BridgeResponse response;
                    BridgeRequest request = null;
                    try
                    {
                        request = BridgeJson.DeserializeRequest(line);
                        response = await _handler(request).ConfigureAwait(false);
                    }
                    catch (BridgeProtocolException ex)
                    {
                        response = BridgeResponse.Fail(request?.Id, ErrorCodes.InvalidRequest, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        response = BridgeResponse.Fail(request?.Id, ErrorCodes.EditorUnavailable, ex.Message);
                    }

                    await WriteLine(stream, BridgeJson.SerializeResponse(response), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Reads newline-delimited lines from a stream, buffering in 64KB chunks and
        // retaining any bytes read past a newline for the next call on the same connection.
        private sealed class PipeLineReader
        {
            private const int ReadBufferSize = 64 * 1024;
            private const long MaxLineBytes = 512L * 1024 * 1024;

            private readonly Stream _stream;
            private readonly byte[] _chunk = new byte[ReadBufferSize];
            private int _chunkLength;
            private int _chunkOffset;

            public PipeLineReader(Stream stream)
            {
                _stream = stream;
            }

            public async Task<string> ReadLine(CancellationToken cancellationToken)
            {
                using (var buffer = new MemoryStream())
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (_chunkOffset >= _chunkLength)
                        {
                            _chunkLength = await _stream.ReadAsync(_chunk, 0, _chunk.Length, cancellationToken).ConfigureAwait(false);
                            _chunkOffset = 0;
                            if (_chunkLength == 0)
                            {
                                return buffer.Length == 0 ? null : Decode(buffer);
                            }
                        }

                        while (_chunkOffset < _chunkLength)
                        {
                            var b = _chunk[_chunkOffset++];
                            if (b == (byte)'\n')
                            {
                                return Decode(buffer);
                            }

                            buffer.WriteByte(b);
                            if (buffer.Length > MaxLineBytes)
                            {
                                throw new IOException($"HammerTime MCP bridge request exceeded {MaxLineBytes} bytes without a newline.");
                            }
                        }
                    }

                    return null;
                }
            }

            private static string Decode(MemoryStream buffer)
            {
                return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF').TrimEnd('\r');
            }
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
