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
        private readonly Action<string> _log;
        private Mutex _ownerLock;
        private Task _acceptLoop;

        /// <param name="log">Where unexpected failures are reported; null discards them.</param>
        public McpNamedPipeServer(string pipeName, Func<BridgeRequest, Task<BridgeResponse>> handler, Action<string> log = null)
        {
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _log = log;
            _cancellation = new CancellationTokenSource();
        }

        public void Start()
        {
            if (!TryClaimPipeName())
            {
                _log?.Invoke("Pipe " + _pipeName + " is already served by another HammerTime editor; this editor's MCP bridge is not listening. Close the other editor and restart this one to use the bridge here.");
                return;
            }
            _acceptLoop = Task.Run(() => AcceptLoop(_cancellation.Token));
        }

        /// <summary>
        /// Named pipe servers of the same name in two editors would both accept clients, so requests would reach
        /// either editor at random. A named mutex (released by the OS when the process exits) marks the owner.
        /// </summary>
        private bool TryClaimPipeName()
        {
            try
            {
                var mutex = new Mutex(false, @"Local\HammerTime.Mcp.Pipe." + _pipeName.Replace('\\', '_'), out var createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    return false;
                }
                _ownerLock = mutex;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false; // exists, created by another security context
            }
            catch (Exception ex)
            {
                // The guard is advisory: never let it keep the bridge from starting.
                _log?.Invoke("Pipe owner check failed for " + _pipeName + ": " + ex.Message);
                return true;
            }
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
            var retryDelayMs = 250;
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream stream = null;
                try
                {
                    // Inside the try: a failing constructor must not end the loop (the bridge would die silently).
                    stream = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    retryDelayMs = 250;
                    // No token here: a cancelled Task.Run would never run the delegate and the stream would leak.
                    var connection = stream;
                    stream = null;
                    _ = Task.Run(() => HandleConnection(connection, cancellationToken));
                }
                catch (Exception ex)
                {
                    stream?.Dispose();
                    if (cancellationToken.IsCancellationRequested) return;
                    _log?.Invoke("Pipe accept failed on " + _pipeName + " (retrying in " + retryDelayMs + " ms): " + ex.Message);
                    try
                    {
                        await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    retryDelayMs = Math.Min(retryDelayMs * 2, 5000);
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
                try
                {
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

                        string payload;
                        try
                        {
                            payload = BridgeJson.SerializeResponse(response);
                        }
                        catch (Exception ex)
                        {
                            // A result that cannot be serialised must still answer the request.
                            _log?.Invoke("Response serialisation failed for " + request?.Method + ": " + ex);
                            payload = BridgeJson.SerializeResponse(BridgeResponse.Fail(request?.Id, ErrorCodes.EditorUnavailable, "The bridge could not serialise the result: " + ex.Message));
                        }
                        await WriteLine(stream, payload, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (IOException) { /* client went away (or sent an oversized line) */ }
                catch (ObjectDisposedException) { /* shutting down */ }
                catch (OperationCanceledException) { /* shutting down */ }
                catch (Exception ex) { _log?.Invoke("Pipe connection failed: " + ex); }
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
            _ownerLock?.Dispose();
            _ownerLock = null;
        }
    }
}
