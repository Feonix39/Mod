﻿using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common;
using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.IndexedZiPatch
{
    public class IndexedZiPatchInstaller : IDisposable
    {
        private readonly object SyncRoot = new();
        public readonly IndexedZiPatchIndex Index;
        public readonly List<SortedSet<Tuple<int, int>>> MissingPartIndicesPerPatch = new();
        public readonly List<SortedSet<int>> MissingPartIndicesPerTargetFile = new();
        public readonly SortedSet<int> TooLongTargetFiles = new();

        public int ProgressReportInterval = 250;
        private readonly List<FileInfo> TargetFiles = new();

        public delegate void OnCorruptionFoundDelegate(IndexedZiPatchPartLocator part, IndexedZiPatchPartLocator.VerifyDataResult result);
        public delegate void OnVerifyProgressDelegate(int targetIndex, long progress, long max);
        public delegate void OnInstallProgressDelegate(int sourceIndex, long progress, long max);

        public event OnCorruptionFoundDelegate OnCorruptionFound;
        public event OnVerifyProgressDelegate OnVerifyProgress;
        public event OnInstallProgressDelegate OnInstallProgress;

        public IndexedZiPatchInstaller(IndexedZiPatchIndex def)
        {
            Index = def;
            foreach (var _ in def.Targets)
            {
                MissingPartIndicesPerTargetFile.Add(new());
                TargetFiles.Add(null);
            }
            foreach (var _ in def.Sources)
                MissingPartIndicesPerPatch.Add(new());
        }

        public async Task VerifyFiles(int concurrentCount = 8, CancellationToken? cancellationToken = null)
        {
            CancellationTokenSource localCancelSource = new();

            if (cancellationToken.HasValue)
                cancellationToken.Value.Register(() => localCancelSource?.Cancel());

            List<Task> verifyTasks = new();
            try
            {
                long progressCounter = 0;
                long progressMax = Index.Targets.Select(x => x.FileSize).Sum();

                Queue<int> pendingTargetIndices = new();
                for (int i = 0; i < Index.Length; i++)
                    pendingTargetIndices.Enqueue(i);

                Task progressReportTask = null;
                while (verifyTasks.Any() || pendingTargetIndices.Any())
                {
                    localCancelSource.Token.ThrowIfCancellationRequested();

                    while (pendingTargetIndices.Any() && verifyTasks.Count < concurrentCount)
                    {
                        var targetIndex = pendingTargetIndices.Dequeue();
                        var fileInfo = TargetFiles[targetIndex];
                        if (fileInfo == null)
                            continue;

                        verifyTasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                var file = Index[targetIndex];
                                if (fileInfo.Length > file.FileSize)
                                    TooLongTargetFiles.Add(targetIndex);

                                using var stream = fileInfo.OpenRead();
                                for (var j = 0; j < file.Count; ++j)
                                {
                                    localCancelSource.Token.ThrowIfCancellationRequested();

                                    var verifyResult = file[j].Verify(stream);
                                    lock (SyncRoot)
                                    {
                                        progressCounter += file[j].TargetSize;
                                        switch (verifyResult)
                                        {
                                            case IndexedZiPatchPartLocator.VerifyDataResult.Pass:
                                                break;

                                            case IndexedZiPatchPartLocator.VerifyDataResult.FailUnverifiable:
                                                throw new Exception($"{file.RelativePath}:{file[j].TargetOffset}:{file[j].TargetEnd}: Should not happen; unverifiable due to insufficient source data");

                                            case IndexedZiPatchPartLocator.VerifyDataResult.FailNotEnoughData:
                                            case IndexedZiPatchPartLocator.VerifyDataResult.FailBadData:
                                                if (file[j].IsFromSourceFile)
                                                    MissingPartIndicesPerPatch[file[j].SourceIndex].Add(Tuple.Create(file[j].TargetIndex, j));
                                                MissingPartIndicesPerTargetFile[file[j].TargetIndex].Add(j);
                                                OnCorruptionFound?.Invoke(file[j], verifyResult);
                                                break;
                                        }
                                    }
                                }
                            }
                            catch (FileNotFoundException)
                            {
                                lock (SyncRoot)
                                {
                                    MarkFileAsMissing(targetIndex);
                                }
                            }
                        }));
                    }

                    if (progressReportTask == null || progressReportTask.IsCompleted)
                    {
                        progressReportTask = Task.Delay(ProgressReportInterval, localCancelSource.Token);
                        OnVerifyProgress?.Invoke(Math.Max(0, Index.Length - pendingTargetIndices.Count - verifyTasks.Count - 1), progressCounter, progressMax);
                    }

                    verifyTasks.Add(progressReportTask);
                    await Task.WhenAny(verifyTasks);
                    verifyTasks.RemoveAt(verifyTasks.Count - 1);
                    if (verifyTasks.FirstOrDefault(x => x.IsFaulted) is Task x)
                        throw x.Exception;
                    verifyTasks.RemoveAll(x => x.IsCompleted);
                }
            }
            finally
            {
                foreach (var task in verifyTasks)
                {
                    if (task.IsCompleted)
                        continue;
                    try
                    {
                        await task;
                    }
                    catch (Exception)
                    {
                        // ignore
                    }
                }
                localCancelSource.Cancel();
                localCancelSource.Dispose();
                localCancelSource = null;
            }
        }

        public void MarkFileAsMissing(int targetIndex)
        {
            var file = Index[targetIndex];
            for (var i = 0; i < file.Count; ++i)
            {
                if (file[i].IsFromSourceFile)
                    MissingPartIndicesPerPatch[file[i].SourceIndex].Add(Tuple.Create(targetIndex, i));
                MissingPartIndicesPerTargetFile[targetIndex].Add(i);
            }
        }

        public void SetTargetFile(int targetIndex, FileInfo fileInfo)
        {
            TargetFiles[targetIndex] = fileInfo;
        }

        public void SetTargetFilesFromAllInRootPath(string rootPath)
        {
            Dispose();
            for (var i = 0; i < Index.Length; i++)
            {
                try
                {
                    SetTargetFile(i, new FileInfo(Path.Combine(rootPath, Index[i].RelativePath)));
                }
                catch (FileNotFoundException)
                {
                    MarkFileAsMissing(i);
                }
                catch (DirectoryNotFoundException)
                {
                    MarkFileAsMissing(i);
                }
            }
        }

        public void SetTargetFilesFromMissingFilesInRootPath(string rootPath)
        {
            Dispose();
            for (var i = 0; i < Index.Length; i++)
            {
                if (MissingPartIndicesPerTargetFile[i].Count == 0 && !TooLongTargetFiles.Contains(i))
                    continue;
                var file = Index[i];
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(rootPath, file.RelativePath)));
                SetTargetFile(i, new FileInfo(Path.Combine(rootPath, file.RelativePath)));
            }
        }

        public async Task RepairNonPatchData(CancellationToken? cancellationToken = null)
        {
            await Task.Run(() =>
            {
                for (int i = 0, i_ = Index.Length; i < i_; i++)
                {
                    if (cancellationToken.HasValue)
                        cancellationToken.Value.ThrowIfCancellationRequested();
                    if (TargetFiles[i] == null)
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(TargetFiles[i].FullName));
                    using var stream = TargetFiles[i].Open(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

                    var file = Index[i];
                    foreach (var partIndex in MissingPartIndicesPerTargetFile[i])
                    {
                        if (cancellationToken.HasValue)
                            cancellationToken.Value.ThrowIfCancellationRequested();

                        var part = file[partIndex];
                        if (part.IsFromSourceFile)
                            continue;

                        part.Repair(stream, (Stream)null);
                    }
                    stream.SetLength(file.FileSize);
                }
            });
        }

        public void WriteVersionFiles(string localRootPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(localRootPath, Index.VersionFileVer)));
            using (var writer = new StreamWriter(new FileStream(Path.Combine(localRootPath, Index.VersionFileVer), FileMode.Create, FileAccess.Write)))
                writer.Write(Index.VersionName);
            using (var writer = new StreamWriter(new FileStream(Path.Combine(localRootPath, Index.VersionFileBck), FileMode.Create, FileAccess.Write)))
                writer.Write(Index.VersionName);
        }

        public abstract class InstallTaskConfig : IDisposable
        {
            public long ProgressMax { get; protected set; }
            public long ProgressValue { get; protected set; }
            public readonly IndexedZiPatchIndex Index;
            public readonly IndexedZiPatchInstaller Installer;
            public readonly int SourceIndex;

            private readonly FileStream[] TargetStreams;

            public InstallTaskConfig(IndexedZiPatchInstaller installer, int sourceIndex)
            {
                Index = installer.Index;
                Installer = installer;
                SourceIndex = sourceIndex;
                TargetStreams = new FileStream[installer.TargetFiles.Count];
            }

            public abstract bool ShouldReattempt { get; }

            public abstract Task Repair(CancellationToken? cancellationToken = null);

            protected FileStream GetFile(int targetIndex)
            {
                if (Installer.TargetFiles[targetIndex] == null)
                    return null;
                if (TargetStreams[targetIndex] == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Installer.TargetFiles[targetIndex].FullName));
                    return TargetStreams[targetIndex] = Installer.TargetFiles[targetIndex].Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                }
                return TargetStreams[targetIndex];
            }

            public virtual void Dispose()
            {
                for (var i = 0; i < TargetStreams.Length; i++)
                {
                    TargetStreams[i]?.Dispose();
                    TargetStreams[i] = null;
                }
            }
        }

        public class HttpInstallTaskConfig : InstallTaskConfig
        {
            public readonly HttpClient Client;
            public readonly string SourceUrl;
            public readonly List<Tuple<int, int>> TargetPartIndices;
            private readonly List<long> TargetPartOffsets;
            private readonly string Sid;
            private int AttemptIndex = 0;

            public HttpInstallTaskConfig(IndexedZiPatchInstaller installer, int sourceIndex, HttpClient client, string sourceUrl, string sid, IEnumerable<Tuple<int, int>> targetPartIndices)
                : base(installer, sourceIndex)
            {
                Client = client;
                SourceUrl = sourceUrl;
                Sid = sid;
                TargetPartIndices = targetPartIndices.ToList();
                TargetPartIndices.Sort((x, y) => Index[x.Item1][x.Item2].SourceOffset.CompareTo(Index[y.Item1][y.Item2].SourceOffset));
                TargetPartOffsets = TargetPartIndices.Select(x => Index[x.Item1][x.Item2].SourceOffset).ToList();

                long totalTargetSize = 0;
                foreach (var (targetIndex, partIndex) in TargetPartIndices)
                    totalTargetSize += Index[targetIndex][partIndex].TargetSize;
                ProgressMax = totalTargetSize;
            }

            public override bool ShouldReattempt => AttemptIndex < 8;

            public override async Task Repair(CancellationToken? cancellationToken = null)
            {
                if (AttemptIndex >= 2)
                {
                    // Exponential backoff
                    if (cancellationToken.HasValue)
                        await Task.Delay(1000 * (1 << Math.Min(5, AttemptIndex - 2)), cancellationToken.Value);
                    else
                        await Task.Delay(1000 * (1 << Math.Min(5, AttemptIndex - 2)));
                }
                AttemptIndex++;
                var offsets = new List<Tuple<long, long>>();
                offsets.Clear();
                foreach (var (targetIndex, partIndex) in TargetPartIndices)
                    offsets.Add(Tuple.Create(Index[targetIndex][partIndex].SourceOffset, Math.Min(Index.GetSourceLastPtr(SourceIndex), Index[targetIndex][partIndex].MaxSourceEnd)));
                offsets.Sort();

                if (!offsets.Any())
                    return;

                for (int j = 1; j < offsets.Count;)
                {
                    if (offsets[j].Item1 - offsets[j - 1].Item2 < 4096)
                    {
                        offsets[j - 1] = Tuple.Create(offsets[j - 1].Item1, offsets[j].Item2);
                        offsets.RemoveAt(j);
                    }
                    else
                        j += 1;
                }

                using HttpRequestMessage req = new(HttpMethod.Get, SourceUrl);
                req.Headers.Add("Range", "bytes=" + string.Join(",", offsets.Select(x => $"{x.Item1}-{x.Item2 - 1}")));
                if (Sid != null)
                    req.Headers.Add("X-Patch-Unique-Id", Sid);
                req.Headers.Add("User-Agent", Constants.PatcherUserAgent);
                req.Headers.Add("Connection", "Keep-Alive");
                using var resp = new MultipartRequestHandler(await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead));

                List<Stream> ss = new();
                while (true)
                {
                    if (cancellationToken.HasValue)
                        cancellationToken.Value.ThrowIfCancellationRequested();

                    using var n = await resp.NextPart(cancellationToken);
                    ss.Add(n);
                    if (n == null)
                        break;

                    var fromTupleIndex = TargetPartOffsets.BinarySearch(n.AvailableFromOffset);
                    if (fromTupleIndex < 0)
                        fromTupleIndex = ~fromTupleIndex;
                    while (fromTupleIndex > 0 && TargetPartOffsets[fromTupleIndex - 1] >= n.AvailableFromOffset)
                        fromTupleIndex--;

                    for (var i = fromTupleIndex; i < TargetPartOffsets.Count && TargetPartOffsets[i] < n.AvailableToOffset; i++)
                    {
                        if (cancellationToken.HasValue)
                            cancellationToken.Value.ThrowIfCancellationRequested();

                        var (targetIndex, partIndex) = TargetPartIndices[i];
                        var part = Index[targetIndex][partIndex];
                        if (part.SourceIndex != SourceIndex)
                            throw new InvalidOperationException("part.SourceIndex != SourceIndex");
                        if (part.TargetIndex != targetIndex)
                            throw new InvalidOperationException("part.TargetIndex != targetIndex");
                        ProgressValue += part.TargetSize;

                        var targetStream = GetFile(part.TargetIndex);
                        if (targetStream != null)
                        {
                            try
                            {
                                part.Repair(targetStream, n);
                            }
                            catch (IndexedZiPatchPartLocator.InsufficientReconstructionDataException)
                            {
                                break;
                            }
                        }

                        TargetPartIndices.RemoveAt(i);
                        TargetPartOffsets.RemoveAt(i);
                        i--;
                    }
                }
                if (TargetPartIndices.Any())
                    throw new IOException("Missing target part remains");
            }
        }

        public class StreamInstallTaskConfig : InstallTaskConfig
        {
            public readonly Stream SourceStream;
            public readonly IList<Tuple<long, long>> SourceOffsets;
            public readonly List<Tuple<int, int>> TargetPartIndices;

            public StreamInstallTaskConfig(IndexedZiPatchInstaller installer, int sourceIndex, Stream sourceStream, IEnumerable<Tuple<int, int>> targetPartIndices)
                : base(installer, sourceIndex)
            {
                SourceStream = sourceStream;
                TargetPartIndices = targetPartIndices.ToList();
                long totalTargetSize = 0;
                foreach (var (targetIndex, partIndex) in TargetPartIndices)
                    totalTargetSize += Index[targetIndex][partIndex].TargetSize;
                ProgressMax = totalTargetSize;
            }

            public override bool ShouldReattempt => false;

            public override async Task Repair(CancellationToken? cancellationToken)
            {
                await Task.Run(() =>
                {
                    for (var i = 0; i < TargetPartIndices.Count; i++)
                    {
                        var (targetIndex, partIndex) = TargetPartIndices[i];
                        if (cancellationToken.HasValue)
                            cancellationToken.Value.ThrowIfCancellationRequested();
                        var part = Index[targetIndex][partIndex];
                        if (part.SourceIndex != SourceIndex)
                            throw new InvalidOperationException("part.SourceIndex != SourceIndex");
                        if (part.TargetIndex != targetIndex)
                            throw new InvalidOperationException("part.TargetIndex != targetIndex");
                        ProgressValue += part.TargetSize;

                        var targetStream = GetFile(part.TargetIndex);
                        if (targetStream != null)
                            part.Repair(targetStream, SourceStream);
                        TargetPartIndices.RemoveAt(0);
                        i--;
                    }
                });
            }

            public override void Dispose()
            {
                SourceStream.Dispose();
                base.Dispose();
            }
        }

        private readonly List<InstallTaskConfig> InstallTaskConfigs = new();

        public void QueueInstall(int sourceIndex, HttpClient client, string sourceUrl, string sid, ISet<Tuple<int, int>> targetPartIndices)
        {
            if (targetPartIndices.Any())
                InstallTaskConfigs.Add(new HttpInstallTaskConfig(this, sourceIndex, client, sourceUrl, sid == "" ? null : sid, targetPartIndices));
        }

        public void QueueInstall(int sourceIndex, HttpClient client, string sourceUrl, string sid, int splitBy = 8, int maxPartsPerRequest = 1024)
        {
            var indices = MissingPartIndicesPerPatch[sourceIndex];
            var indicesPerRequest = Math.Min((int)Math.Ceiling(1.0 * indices.Count / splitBy), maxPartsPerRequest);
            for (int j = 0; j < indices.Count; j += indicesPerRequest)
                QueueInstall(sourceIndex, client, sourceUrl, sid, indices.Skip(j).Take(Math.Min(indicesPerRequest, indices.Count - j)).ToHashSet());
        }

        public void QueueInstall(int sourceIndex, Stream stream, ISet<Tuple<int, int>> targetPartIndices)
        {
            if (targetPartIndices.Any())
                InstallTaskConfigs.Add(new StreamInstallTaskConfig(this, sourceIndex, stream, targetPartIndices));
        }

        public void QueueInstall(int sourceIndex, FileInfo file, ISet<Tuple<int, int>> targetPartIndices)
        {
            if (targetPartIndices.Any())
                QueueInstall(sourceIndex, file.OpenRead(), targetPartIndices);
        }

        public void QueueInstall(int sourceIndex, FileInfo file, int splitBy = 8)
        {
            var indices = MissingPartIndicesPerPatch[sourceIndex];
            var indicesPerRequest = (int)Math.Ceiling(1.0 * indices.Count / splitBy);
            for (int j = 0; j < indices.Count; j += indicesPerRequest)
                QueueInstall(sourceIndex, file, indices.Skip(j).Take(Math.Min(indicesPerRequest, indices.Count - j)).ToHashSet());
        }

        public async Task Install(int concurrentCount, CancellationToken? cancellationToken = null)
        {
            if (!InstallTaskConfigs.Any())
                return;

            long progressMax = InstallTaskConfigs.Select(x => x.ProgressMax).Sum();

            Task progressReportTask = null;
            Queue<InstallTaskConfig> pendingTaskConfigs = new();
            foreach (var x in InstallTaskConfigs)
                pendingTaskConfigs.Enqueue(x);

            Dictionary<Task, InstallTaskConfig> runningTasks = new();

            var exceptions = new List<Exception>();

            while (pendingTaskConfigs.Any() || runningTasks.Any())
            {
                if (cancellationToken.HasValue)
                    cancellationToken.Value.ThrowIfCancellationRequested();

                while (pendingTaskConfigs.Any() && runningTasks.Count < concurrentCount)
                {
                    var config = pendingTaskConfigs.Dequeue();
                    runningTasks[config.Repair(cancellationToken)] = config;
                }

                var taskIndex = Math.Max(0, InstallTaskConfigs.Count - pendingTaskConfigs.Count - runningTasks.Count - 1);
                var sourceIndexForProgressDisplay = InstallTaskConfigs[Math.Min(taskIndex, InstallTaskConfigs.Count - 1)].SourceIndex;
                OnInstallProgress?.Invoke(sourceIndexForProgressDisplay, InstallTaskConfigs.Select(x => x.ProgressValue).Sum(), progressMax);

                if (progressReportTask == null || progressReportTask.IsCompleted)
                    progressReportTask = cancellationToken.HasValue ? Task.Delay(ProgressReportInterval, cancellationToken.Value) : Task.Delay(ProgressReportInterval);
                runningTasks[progressReportTask] = null;
                await Task.WhenAny(runningTasks.Keys);
                foreach (var kvp in runningTasks)
                {
                    if (!kvp.Key.IsFaulted || kvp.Value == null)
                        continue;

                    if (kvp.Value.ShouldReattempt)
                    {
                        Log.Warning(kvp.Key.Exception, "Exception occurred while loading part; trying again");
                        pendingTaskConfigs.Enqueue(kvp.Value);
                    }
                    else
                    {
                        exceptions.Add(kvp.Key.Exception);
                    }
                }
                runningTasks.Keys.Where(p => p.IsCompleted || p.IsCanceled || p.IsFaulted || p == progressReportTask).ToList().ForEach(p => {
                    runningTasks[p]?.Dispose();
                    runningTasks.Remove(p);
                });
            }
            await RepairNonPatchData();

            if (exceptions.Count == 1)
                throw exceptions[0];
            else if (exceptions.Count > 1)
                throw new AggregateException("More than one error has occurred while installing.", exceptions);
        }

        public void Dispose()
        {
            foreach (var item in InstallTaskConfigs)
                item.Dispose();
            InstallTaskConfigs.Clear();
        }
    }
}