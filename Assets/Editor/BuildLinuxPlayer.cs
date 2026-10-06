// A command-line player build, so a release can be produced without opening the
// editor UI. Unity has no built-in "build what the Build Settings say" switch,
// so this is the one static entry point the batch command calls:
//
//   Unity -batchmode -quit -projectPath . -buildTarget Linux64 \
//         -executeMethod BuildLinuxPlayer.Build
//
// The output path is taken from ROBOTSNAP_BUILD_OUTPUT when it is set, and
// defaults to Builds/Linux/robotsnap-unity.x86_64. A failed build exits with a
// non-zero code, so a script - or a CI job - can tell the difference between a
// player that was produced and one that was not.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildLinuxPlayer
{
    public static void Build()
    {
        string output = Environment.GetEnvironmentVariable("ROBOTSNAP_BUILD_OUTPUT");
        if (string.IsNullOrEmpty(output))
        {
            output = Path.Combine("Builds", "Linux", "robotsnap-unity.x86_64");
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Only the scenes the Build Settings enable, in their own order: the
        // build reproduces what the editor's Build button would produce.
        var scenes = new List<string>();
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if (scene.enabled)
            {
                scenes.Add(scene.path);
            }
        }

        if (scenes.Count == 0)
        {
            Debug.LogError("no enabled scene in the Build Settings: nothing to build");
            EditorApplication.Exit(2);
            return;
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes.ToArray(),
            locationPathName = output,
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        if (summary.result != BuildResult.Succeeded)
        {
            Debug.LogError(
                $"linux player build failed: {summary.result}, "
                + $"{summary.totalErrors} error(s), output {summary.outputPath}"
            );
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log(
            $"linux player build ok: {summary.outputPath} "
            + $"({summary.totalSize / (1024 * 1024)} MiB, "
            + $"{summary.totalTime.TotalSeconds:F0} s)"
        );
        EditorApplication.Exit(0);
    }
}
