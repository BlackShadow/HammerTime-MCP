using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HammerTime.Mcp.Shared;
using LogicAndTrick.Oy;
using Sledge.BspEditor.Compile;
using Sledge.BspEditor.Documents;

namespace HammerTime.Mcp.Plugin
{
    // The editor's Batch.Successful starts true and BatchProcess does not check exit codes.
    // Keep its export/result callbacks, but verify the compiler processes and their output.
    internal sealed class CompilePipeline
    {
        internal static readonly string[] StageNames = { "CSG", "BSP", "VIS", "RAD" };
        private readonly List<CompilerStep> _compilers = new List<CompilerStep>();
        private bool _outputVerified;
        private string _bspPath;
        private DateTime? _previousWriteTime;
        private long _previousLength;

        internal bool Successful => _outputVerified && _compilers.All(x => x.Completed && x.ExitCode == 0);

        internal static CompilePipeline Prepare(Batch batch, IList<string> stages, Func<string, string, Task> writeLog)
        {
            if (batch?.Steps == null || stages.Count == 0)
                throw ConfigurationError("The document environment did not create a compiler batch.");

            var processes = batch.Steps.Where(x => x.StepType == BatchStepType.RunBuildExecutable).ToList();
            if (processes.Count != stages.Count || processes.Any(x => !(x is BatchProcess)))
                throw ConfigurationError("The document environment did not create all requested compiler stages.");

            if (!batch.Steps.Any(x => x.StepType == BatchStepType.ExportDocument) ||
                !batch.Steps.Any(x => x.StepType == BatchStepType.CheckIfSuccessful))
                throw ConfigurationError("The compiler batch is missing map export or output validation.");

            var pipeline = new CompilePipeline();
            for (var i = 0; i < processes.Count; i++)
            {
                var process = (BatchProcess)processes[i];
                if (string.IsNullOrWhiteSpace(process.Process) || !File.Exists(process.Process))
                    throw ConfigurationError($"{stages[i]} compiler executable is not configured or does not exist: '{process.Process}'. Configure the environment's compiler tools.");
                var compiler = new CompilerStep(stages[i], process, writeLog);
                pipeline._compilers.Add(compiler);
                batch.Steps[batch.Steps.IndexOf(process)] = compiler;
            }

            var firstCompiler = batch.Steps.IndexOf(pipeline._compilers[0]);
            batch.Steps.Insert(firstCompiler, new BatchCallback(BatchStepType.CheckIfSuccessful, (b, d) =>
            {
                if (!b.Variables.TryGetValue("MapFile", out var mapFile) || string.IsNullOrWhiteSpace(mapFile))
                    throw ConfigurationError("Map export did not provide an output filename.");
                pipeline._bspPath = Path.ChangeExtension(mapFile, ".bsp");
                var previous = new FileInfo(pipeline._bspPath);
                if (previous.Exists)
                {
                    pipeline._previousWriteTime = previous.LastWriteTimeUtc;
                    pipeline._previousLength = previous.Length;
                }
                return Task.CompletedTask;
            }));

            // Verify before the editor copies results, removes its temporary directory or runs the game.
            var lastCompiler = batch.Steps.IndexOf(pipeline._compilers.Last());
            batch.Steps.Insert(lastCompiler + 1, new BatchCallback(BatchStepType.CheckIfSuccessful, async (b, d) =>
            {
                var output = new FileInfo(pipeline._bspPath);
                var fresh = output.Exists && output.Length > 0 &&
                    (pipeline._previousWriteTime == null || output.LastWriteTimeUtc != pipeline._previousWriteTime || output.Length != pipeline._previousLength);
                pipeline._outputVerified = fresh;
                if (!fresh)
                {
                    b.Successful = false;
                    await writeLog("Error", $"Compilation did not produce or update a non-empty BSP: {pipeline._bspPath}");
                }
                if (!pipeline.Successful) b.Successful = false;
            }));
            return pipeline;
        }

        private static BridgeCommandException ConfigurationError(string message)
        {
            return new BridgeCommandException(ErrorCodes.InvalidOperation, message);
        }

        private sealed class CompilerStep : BatchStep
        {
            private readonly string _name;
            private readonly BatchProcess _source;
            private readonly Func<string, string, Task> _writeLog;
            internal bool Completed { get; private set; }
            internal int? ExitCode { get; private set; }
            public override BatchStepType StepType => BatchStepType.RunBuildExecutable;

            internal CompilerStep(string name, BatchProcess source, Func<string, string, Task> writeLog)
            {
                _name = name;
                _source = source;
                _writeLog = writeLog;
            }

            public override async Task Run(Batch batch, MapDocument document)
            {
                if (!batch.Successful) return;
                string Expand(string value)
                {
                    foreach (var variable in batch.Variables)
                        value = value.Replace("{" + variable.Key + "}", variable.Value);
                    return value;
                }

                using (var process = new Process())
                using (var cancellation = new CancellationTokenSource())
                using (Oy.Subscribe("Compile:Interrupt", () => cancellation.Cancel()))
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = Expand(_source.Process),
                        Arguments = Expand(_source.Arguments ?? ""),
                        WorkingDirectory = Expand(_source.WorkingDirectory ?? "{WorkingDirectory}"),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    var started = false;
                    var stdout = Task.CompletedTask;
                    var stderr = Task.CompletedTask;
                    try
                    {
                        await _writeLog("Information", $"{_name}: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                        cancellation.Token.ThrowIfCancellationRequested();
                        started = process.Start();
                        if (!started) throw new InvalidOperationException("The compiler process did not start.");
                        stdout = CopyOutput(process.StandardOutput, "Output");
                        stderr = CopyOutput(process.StandardError, "Error");
                        await process.WaitForExitAsync(cancellation.Token);
                        await Task.WhenAll(stdout, stderr);
                        cancellation.Token.ThrowIfCancellationRequested();
                        ExitCode = process.ExitCode;
                        Completed = true;
                        await _writeLog("Information", $"{_name} exited with code {ExitCode}.");
                        if (ExitCode != 0)
                        {
                            batch.Successful = false;
                            await _writeLog("Error", $"{_name} compilation failed (exit code {ExitCode}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        batch.Successful = false;
                        if (started)
                        {
                            if (!process.HasExited) process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync();
                            await Task.WhenAll(stdout, stderr);
                        }
                        await _writeLog("Error", ex is OperationCanceledException
                            ? $"{_name} compilation was interrupted."
                            : $"{_name} compiler failed: {ex.Message}");
                    }
                }
            }

            private async Task CopyOutput(StreamReader reader, string kind)
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                    await _writeLog(kind, line);
            }
        }
    }
}
