﻿using System.Diagnostics;
using SeeSharp.Experiments;

SceneRegistry.AddSource("../../GuidingExperiments/Scenes");
Benchmark benchmark = new(new GuidedPathTracer.Experiment(128, int.MaxValue), new() {
    // SceneRegistry.LoadScene("HomeOffice"),
    // SceneRegistry.LoadScene("RoughGlassesIndirect", maxDepth: 10),
    SceneRegistry.LoadScene("RoughGlasses", maxDepth: 10),
    // SceneRegistry.LoadScene("LampCaustic", maxDepth: 10),
    // SceneRegistry.LoadScene("TargetPractice"),
    // SceneRegistry.LoadScene("ModernHall"),
    // SceneRegistry.LoadScene("CountryKitchen"),
    // SceneRegistry.LoadScene("ModernLivingRoom", maxDepth: 10),
    // SceneRegistry.LoadScene("Pool", maxDepth: 10),
}, "Results", 640, 480, SeeSharp.Image.FrameBuffer.Flags.SendToTev);
benchmark.Run(skipReference: false);
