using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using System;
using System.IO;

public static class WACVRCustomBuild
{
    public static void BuildWindows64()
    {
        string projectRoot =
            Directory.GetParent(Application.dataPath).FullName;

        string outputRoot =
            Environment.GetEnvironmentVariable(
                "WACVR_CUSTOM_OUTPUT"
            );

        if (String.IsNullOrEmpty(outputRoot))
        {
            outputRoot = Path.Combine(
                Directory.GetParent(projectRoot).FullName,
                "WACVR_Custom_V0.2.3_COMPILE"
            );
        }

        if (Directory.Exists(outputRoot))
            Directory.Delete(outputRoot, true);

        Directory.CreateDirectory(outputRoot);

        string exePath =
            Path.Combine(outputRoot, "WACVR_Core.exe");

        var scenes =
            new System.Collections.Generic.List<string>();

        foreach (var scene in EditorBuildSettings.scenes)
        {
            if (scene.enabled)
                scenes.Add(scene.path);
        }

        if (scenes.Count == 0)
            throw new Exception(
                "No enabled scenes found in EditorBuildSettings."
            );

        var options = new BuildPlayerOptions
        {
            scenes = scenes.ToArray(),
            locationPathName = exePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        BuildReport report =
            BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception(
                "WACVR custom build failed: " +
                report.summary.result
            );
        }

        File.WriteAllText(
            Path.Combine(
                outputRoot,
                "WACVR_CUSTOM_VERSION.txt"
            ),
            "WACVR Custom V0.2.3 - Waccon-IO Clean Stable / Production\n" +
            "Validated gameplay base: V0.1.2 Frame Timing / OpenXR; V0.1.3 cleanup inherited\n" +
            "Official base: xiaopeng12138/WACVR c98216990436bd8932161d19bc3659719fb0e401\n" +
            "Unity: 2021.3.22f1\n" +
            "Capture Desktop startup: OFF\n" +
            "Touch physics startup: 60 Hz\n" +
            "Hybrid Touch startup: ON\n" +
            "Haptics startup: ON\n" +
            "Swept tracking: HandFollowManager.Target raw transform when available\n" +
            "Frame timing: OpenXR/VDXR owns pacing, global Unity cap disabled\n" +
            "Profile: INTEGRATED SESSION MANAGER / OPENXR UNIVERSAL\n"
        );

        Debug.Log(
            "WACVR CUSTOM V0.2.3 CORE BUILD OK: " +
            exePath
        );
    }
}
