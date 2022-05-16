﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Unix.Compatibility;

namespace XIVLauncher.Common.Unix;

public class UnixDalamudRunner : IDalamudRunner
{
    private readonly CompatibilityTools compatibility;
    private readonly DirectoryInfo dotnetRuntime;

    public UnixDalamudRunner(CompatibilityTools compatibility, DirectoryInfo dotnetRuntime)
    {
        this.compatibility = compatibility;
        this.dotnetRuntime = dotnetRuntime;
    }

    public void Run(Int32 gameProcessID, FileInfo runner, DalamudStartInfo startInfo, DirectoryInfo gamePath, DalamudLoadMethod loadMethod)
    {
        // Wine wants Windows paths here, so we need to fix up the startinfo dirs
        startInfo.WorkingDirectory = compatibility.UnixToWinePath(startInfo.WorkingDirectory);
        startInfo.ConfigurationPath = compatibility.UnixToWinePath(startInfo.ConfigurationPath);
        startInfo.PluginDirectory = compatibility.UnixToWinePath(startInfo.PluginDirectory);
        startInfo.DefaultPluginDirectory = compatibility.UnixToWinePath(startInfo.DefaultPluginDirectory);
        startInfo.AssetDirectory = compatibility.UnixToWinePath(startInfo.AssetDirectory);

        switch (loadMethod)
        {
            case DalamudLoadMethod.EntryPoint:
                throw new NotImplementedException();
                break;

            case DalamudLoadMethod.DllInject:
            {
                var parameters = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(startInfo)));
                var launchArguments = new string[] { runner.FullName, gameProcessID.ToString(), parameters };
                var environment = new Dictionary<string, string>();
                environment.Add("DALAMUD_RUNTIME", compatibility.UnixToWinePath(dotnetRuntime.FullName));
                compatibility.RunInPrefix(launchArguments, environment: environment);
                break;
            }

            default:
                // should not reach
                throw new ArgumentOutOfRangeException();
        }
    }
}