using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Patching.IndexedZiPatch;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Game.Patch
{
    public class PatchVerifier : IDisposable
    {
        private readonly ISettings _settings;
        private readonly int _maxExpansionToCheck;
        private HttpClient _client;
        private CancellationTokenSource _cancellationTokenSource = new();

        private Dictionary<Repository, string> _repoMetaPaths = new();
        private Dictionary<string, object> _patchSources = new();

        private Task _verificationTask;
        private List<Tuple<long, long>> _reportedProgresses = new();

        public int ProgressUpdateInterval { get; private set; }
        public int NumBrokenFiles { get; private set; } = 0;
        public int PatchSetIndex { get; private set; }
        public int PatchSetCount { get; private set; }
        public int TaskIndex { get; private set; }
        public long Progress { get; private set; }
        public long Total { get; private set; }
        public int TaskCount { get; private set; }
        public IndexedZiPatchInstaller.InstallTaskState CurrentMetaInstallState { get; private set; } = IndexedZiPatchInstaller.InstallTaskState.NotStarted;
        public string CurrentFile { get; private set; }
        public long Speed { get; private set; }
        public Exception LastException { get; private set; }

        private const string BASE_URL = "https://raw.githubusercontent.com/goatcorp/patchinfo/main/";

        public enum VerifyState
        {
            Unknown,
            Verify,
            Done,
            Cancelled,
            Error
        }

        private class VerifyVersions
        {
            [JsonProperty("boot")]
            public string Boot { get; set; }

            [JsonProperty("bootRevision")]
            public int BootRevision { get; set; }

            [JsonProperty("game")]
            public string Game { get; set; }

            [JsonProperty("gameRevision")]
            public int GameRevision { get; set; }

            [JsonProperty("ex1")]
            public string Ex1 { get; set; }

            [JsonProperty("ex1Revision")]
            public int Ex1Revision { get; set; }

            [JsonProperty("ex2")]
            public string Ex2 { get; set; }

            [JsonProperty("ex2Revision")]
            public int Ex2Revision { get; set; }

            [JsonProperty("ex3")]
            public string Ex3 { get; set; }

            [JsonProperty("ex3Revision")]
            public int Ex3Revision { get; set; }

            [JsonProperty("ex4")]
            public string Ex4 { get; set; }

            [JsonProperty("ex4Revision")]
            public int Ex4Revision { get; set; }
        }

        public VerifyState State { get; private set; } = VerifyState.Unknown;

        public PatchVerifier(ISettings settings, Launcher.LoginResult loginResult, int progressUpdateInterval, int maxExpansion)
        {
            this._settings = settings;
            _client = new HttpClient();
            ProgressUpdateInterval = progressUpdateInterval;
            _maxExpansionToCheck = maxExpansion;

            SetLoginState(loginResult);
        }

        public void Start()
        {
            Debug.Assert(_repoMetaPaths.Count != 0 && _patchSources.Count != 0);
            Debug.Assert(_verificationTask == null || _verificationTask.IsCompleted);

            _cancellationTokenSource = new();
            _reportedProgresses.Clear();
            NumBrokenFiles = 0;
            PatchSetIndex = 0;
            PatchSetCount = 0;
            TaskIndex = 0;
            Progress = 0;
            Total = 0;
            TaskCount = 0;
            CurrentFile = null;
            Speed = 0;
            CurrentMetaInstallState = IndexedZiPatchInstaller.InstallTaskState.NotStarted;
            LastException = null;

            _verificationTask = Task.Run(this.RunVerifier, _cancellationTokenSource.Token);
        }

        public async Task Cancel()
        {
            _cancellationTokenSource.Cancel();
            if (_verificationTask != null)
                await _verificationTask;
        }

        public async Task WaitForCompletion()
        {
            await _verificationTask;
        }

        private void SetLoginState(Launcher.LoginResult result)
        {
            _patchSources.Clear();

            foreach (var patch in result.PendingPatches)
            {
                var repoName = patch.GetRepoName();
                if (repoName == "ffxiv")
                    repoName = "ex0";

                var file = new FileInfo(Path.Combine(_settings.PatchPath.FullName, patch.GetFilePath()));
                if (file.Exists && file.Length == patch.Length)
                    _patchSources.Add($"{repoName}:{Path.GetFileName(patch.GetFilePath())}", file);
                else
                    _patchSources.Add($"{repoName}:{Path.GetFileName(patch.GetFilePath())}", new Uri(patch.Url));
            }
        }

        private bool AdminAccessRequired(string gameRootPath)
        {
            string tempFn;
            do
            {
                tempFn = Path.Combine(gameRootPath, Guid.NewGuid().ToString());
            } while (File.Exists(tempFn));
            try
            {
                File.WriteAllText(tempFn, "");
                File.Delete(tempFn);
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            return false;
        }

        private void RecordProgressForEstimation()
        {
            var now = DateTime.Now.Ticks;
            _reportedProgresses.Add(Tuple.Create(now, Progress));
            while ((now - _reportedProgresses.First().Item1) > 10 * 1000 * 8000)
                _reportedProgresses.RemoveAt(0);

            var elapsedMs = _reportedProgresses.Last().Item1 - _reportedProgresses.First().Item1;
            if (elapsedMs == 0)
                Speed = 0;
            else
                Speed = (_reportedProgresses.Last().Item2 - _reportedProgresses.First().Item2) * 10 * 1000 * 1000 / elapsedMs;
        }

        private async Task RunVerifier()
        {
            State = VerifyState.Unknown;
            LastException = null;
            try
            {
                var assemblyLocation = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                using var remote = new IndexedZiPatchIndexRemoteInstaller(Path.Combine(assemblyLocation!, "XIVLauncher.PatchInstaller.exe"),
                    AdminAccessRequired(_settings.GamePath.FullName));
                await remote.SetWorkerProcessPriority(ProcessPriorityClass.Idle);

                while (!_cancellationTokenSource.IsCancellationRequested && State != VerifyState.Done)
                {
                    switch (State)
                    {

                        case VerifyState.Unknown:
                            State = VerifyState.Verify;
                            break;
                        case VerifyState.Verify:
                            const int MAX_CONCURRENT_CONNECTIONS_FOR_PATCH_SET = 8;

                            PatchSetIndex = 0;
                            PatchSetCount = _repoMetaPaths.Count;
                            foreach (var metaPath in _repoMetaPaths)
                            {
                                var patchIndex = new IndexedZiPatchIndex(new BinaryReader(new DeflateStream(new FileStream(metaPath.Value, FileMode.Open, FileAccess.Read), CompressionMode.Decompress)));

                                void UpdateVerifyProgress(int targetIndex, long progress, long max)
                                {
                                    CurrentFile = patchIndex[Math.Min(targetIndex, patchIndex.Length - 1)].RelativePath;
                                    TaskIndex = targetIndex;
                                    Progress = Math.Min(progress, max);
                                    Total = max;
                                    RecordProgressForEstimation();
                                }

                                void UpdateInstallProgress(int sourceIndex, long progress, long max, IndexedZiPatchInstaller.InstallTaskState state)
                                {
                                    CurrentFile = patchIndex.Sources[Math.Min(sourceIndex, patchIndex.Sources.Count - 1)];
                                    TaskIndex = sourceIndex;
                                    Progress = Math.Min(progress, max);
                                    Total = max;
                                    CurrentMetaInstallState = state;
                                    RecordProgressForEstimation();
                                }

                                try
                                {
                                    remote.OnVerifyProgress += UpdateVerifyProgress;
                                    remote.OnInstallProgress += UpdateInstallProgress;
                                    await remote.ConstructFromPatchFile(patchIndex, ProgressUpdateInterval);

                                    var fileBroken = new bool[patchIndex.Length].ToList();
                                    var repaired = false;
                                    for (var attemptIndex = 0; attemptIndex < 5; attemptIndex++)
                                    {
                                        CurrentMetaInstallState = IndexedZiPatchInstaller.InstallTaskState.NotStarted;

                                        TaskCount = patchIndex.Length;
                                        Progress = Total = TaskIndex = 0;
                                        _reportedProgresses.Clear();

                                        var adjustedGamePath = Path.Combine(_settings.GamePath.FullName, patchIndex.ExpacVersion == IndexedZiPatchIndex.EXPAC_VERSION_BOOT ? "boot" : "game");

                                        await remote.SetTargetStreamsFromPathReadOnly(adjustedGamePath);
                                        // TODO: check one at a time if random access is slow?
                                        await remote.VerifyFiles(attemptIndex > 0, Environment.ProcessorCount, _cancellationTokenSource.Token);

                                        var missingPartIndicesPerTargetFile = await remote.GetMissingPartIndicesPerTargetFile();
                                        if ((repaired = missingPartIndicesPerTargetFile.All(x => !x.Any())))
                                            break;

                                        for (var i = 0; i < missingPartIndicesPerTargetFile.Count; i++)
                                            if (missingPartIndicesPerTargetFile[i].Any())
                                                fileBroken[i] = true;

                                        TaskCount = patchIndex.Sources.Count;
                                        Progress = Total = TaskIndex = 0;
                                        _reportedProgresses.Clear();
                                        var missing = await remote.GetMissingPartIndicesPerPatch();

                                        await remote.SetTargetStreamsFromPathReadWriteForMissingFiles(adjustedGamePath);
                                        var prefix = patchIndex.ExpacVersion == IndexedZiPatchIndex.EXPAC_VERSION_BOOT ? "boot:" : $"ex{patchIndex.ExpacVersion}:";
                                        for (var i = 0; i < patchIndex.Sources.Count; i++)
                                        {
                                            var patchSourceKey = prefix + patchIndex.Sources[i];

                                            if (!missing[i].Any())
                                            {
                                                Log.Information("Patch file {0} skipped because no missing files had parts depending on this patch file.", patchIndex.Sources[i]);
                                                continue;
                                            }
                                            else
                                            {
                                                Log.Information("Looking for patch file {0} (key: \"{1}\")", patchIndex.Sources[i], patchSourceKey);
                                            }

                                            if (!_patchSources.TryGetValue(patchSourceKey, out var source))
                                                throw new InvalidOperationException($"Key \"{patchSourceKey}\" not found in _patchSources");
                                            else if (source is Uri uri)
                                                await remote.QueueInstall(i, uri, null, MAX_CONCURRENT_CONNECTIONS_FOR_PATCH_SET);
                                            else if (source is FileInfo file)
                                                await remote.QueueInstall(i, file, MAX_CONCURRENT_CONNECTIONS_FOR_PATCH_SET);
                                            else
                                                throw new InvalidOperationException("_patchSources contains non-Uri/FileInfo");
                                        }

                                        CurrentMetaInstallState = IndexedZiPatchInstaller.InstallTaskState.Connecting;
                                        try
                                        {
                                            await remote.Install(MAX_CONCURRENT_CONNECTIONS_FOR_PATCH_SET, _cancellationTokenSource.Token);
                                            await remote.WriteVersionFiles(adjustedGamePath);
                                        }
                                        catch (Exception e)
                                        {
                                            Log.Error(e, "remote.Install");
                                            if (attemptIndex == 4)
                                                throw;
                                        }
                                    }
                                    if (!repaired)
                                        throw new Exception("Failed to repair after 5 attempts");
                                    NumBrokenFiles += fileBroken.Where(x => x).Count();
                                    PatchSetIndex++;
                                }
                                finally
                                {
                                    remote.OnVerifyProgress -= UpdateVerifyProgress;
                                    remote.OnInstallProgress -= UpdateInstallProgress;
                                }
                            }

                            State = VerifyState.Done;
                            break;
                        case VerifyState.Done:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                    State = VerifyState.Cancelled;
                else if (_cancellationTokenSource.IsCancellationRequested)
                    State = VerifyState.Cancelled;
                else if (ex is Win32Exception winex && (uint)winex.HResult == 0x80004005u)  // The operation was canceled by the user (UAC dialog cancellation)
                    State = VerifyState.Cancelled;
                else
                {
                    Log.Error(ex, "Unexpected error occurred in RunVerifier");
                    Log.Information("_patchSources had following:");
                    foreach (var kvp in _patchSources)
                    {
                        if (kvp.Value == null)
                            Log.Information("* \"{0}\" = null", kvp.Key);
                        else
                            Log.Information("* \"{0}\" = {1}({2})", kvp.Key, kvp.Value.GetType(), kvp.Value);
                    }

                    LastException = ex;
                    State = VerifyState.Error;
                }
            }
        }

        public async Task GetPatchMeta()
        {
            _repoMetaPaths.Clear();

            var metaFolder = Path.Combine(Paths.RoamingPath, "patchMeta");
            Directory.CreateDirectory(metaFolder);

            var latestVersionJson = await _client.GetStringAsync(BASE_URL + "latest.json");
            var latestVersion = JsonConvert.DeserializeObject<VerifyVersions>(latestVersionJson);

            await this.GetRepoMeta(Repository.Ffxiv, latestVersion.Game, metaFolder, latestVersion.GameRevision);
            if (_maxExpansionToCheck >= 1)
                await this.GetRepoMeta(Repository.Ex1, latestVersion.Ex1, metaFolder, latestVersion.Ex1Revision);
            if (_maxExpansionToCheck >= 2)
                await this.GetRepoMeta(Repository.Ex2, latestVersion.Ex2, metaFolder, latestVersion.Ex2Revision);
            if (_maxExpansionToCheck >= 3)
                await this.GetRepoMeta(Repository.Ex3, latestVersion.Ex3, metaFolder, latestVersion.Ex3Revision);
            if (_maxExpansionToCheck >= 4)
                await this.GetRepoMeta(Repository.Ex4, latestVersion.Ex4, metaFolder, latestVersion.Ex4Revision);
        }

        private async Task GetRepoMeta(Repository repo, string latestVersion, string baseDir, int patchIndexFileRevision)
        {
            var version = repo.GetVer(_settings.GamePath);
            if (version == Constants.BASE_GAME_VERSION)
                return;

            // TODO: We should not assume that this always has a "D". We should just store them by the patchlist VersionId instead.
            var repoShorthand = repo == Repository.Ffxiv ? "game" : repo.ToString().ToLower();
            var fileName = $"{latestVersion}.patch.index";

            var metaPath = Path.Combine(baseDir, repoShorthand);
            var filePath = Path.Combine(metaPath, fileName) + (patchIndexFileRevision > 0 ? $".v{patchIndexFileRevision}" : "");
            Directory.CreateDirectory(metaPath);

            if (!File.Exists(filePath))
            {
                var request = await _client.GetAsync($"{BASE_URL}{repoShorthand}/{fileName}", _cancellationTokenSource.Token);
                if (request.StatusCode == HttpStatusCode.NotFound)
                    throw new NoVersionReferenceException(repo, version);

                request.EnsureSuccessStatusCode();

                File.WriteAllBytes(filePath, await request.Content.ReadAsByteArrayAsync());
            }

            _repoMetaPaths.Add(repo, filePath);
            Log.Verbose("Downloaded patch index for {Repo}({Version})", repo, version);
        }

        public void Dispose()
        {
            if (_verificationTask != null && !_verificationTask.IsCompleted)
            {
                _cancellationTokenSource.Cancel();
                _verificationTask.Wait();
            }
        }
    }
}
