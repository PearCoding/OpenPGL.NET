using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using SimpleImageIO;
using TinyEmbree;
using SeeSharp.Geometry;
using SeeSharp.Sampling;
using SeeSharp.Shading.Emitters;
using SeeSharp.Integrators.Bidir;
using SeeSharp.Integrators;
using SeeSharp.Integrators.Util;
using SeeSharp;

namespace GuidedPathTracer {
    public class PathTracer : Integrator {
        public UInt32 BaseSeed = 0xC030114;
        public int TotalSpp = 20;
        public uint MinDepth = 1;
        public int NumShadowRays = 1;
        public bool EnableBsdfDI = true;
        public bool RenderTechniquePyramid = false;

        TechPyramid techPyramidRaw;
        TechPyramid techPyramidWeighted;

        DenoiseBuffers denoiseBuffers;

        protected Scene scene;

        /// <summary>
        /// Called once for each complete path from the camera to a light.
        /// The default implementation generates a technique pyramid for the MIS samplers.
        /// </summary>
        public virtual void RegisterSample(Vector2 pixel, RgbColor weight, float misWeight, uint depth,
                                           bool isNextEvent) {
            if (!RenderTechniquePyramid)
                return;
            weight /= TotalSpp;
            int cameraEdges = (int)depth - (isNextEvent ? 1 : 0);
            techPyramidRaw.Add(cameraEdges, 0, (int)depth, pixel, weight);
            techPyramidWeighted.Add(cameraEdges, 0, (int)depth, pixel, weight * misWeight);
        }

        /// <summary>
        /// Called for every surface hit, before any sampling takes place.
        /// </summary>
        protected virtual void OnHit(Ray ray, Hit hit, PathState state) {}

        /// <summary>
        /// Called whenever direct illumination was estimated via next event estimation
        /// </summary>
        protected virtual void OnNextEventResult(Ray ray, SurfacePoint point, PathState state,
                                                 float misWeight, RgbColor estimate) {}

        /// <summary>
        /// Called whenever an emitter was intersected
        /// </summary>
        protected virtual void OnHitLightResult(Ray ray, PathState state, float misWeight, RgbColor emission,
                                                bool isBackground) {}

        /// <summary>
        /// Called before a path is traced, after the initial camera ray was sampled
        /// </summary>
        /// <param name="state">Initial state of the path (only pixel and RNG are set)</param>
        protected virtual void OnStartPath(PathState state) {}

        /// <summary>
        /// Called after a path has finished tracing and its contribution was added to the corresponding pixel.
        /// </summary>
        protected virtual void OnFinishedPath(Vector2 pixel, RNG rng, RadianceEstimate estimate) {}

        /// <summary> Called after the scene was submitted, before rendering starts. </summary>
        protected virtual void OnPrepareRender() {}

        /// <summary>
        /// Called before each iteration (one sample per pixel), after the frame buffer was updated.
        /// </summary>
        protected virtual void OnPreIteration(uint iterIdx) {}

        /// <summary>
        /// Called at the end of each iteration (one sample per pixel), before the frame buffer is updated.
        /// </summary>
        protected virtual void OnPostIteration(uint iterIdx) {}

        protected struct RadianceEstimate {
            public RgbColor Emitted { get; init; }
            public RgbColor Reflected { get; init; }
            public float NextEventPdf { get; init; }

            public RgbColor Outgoing => Emitted + Reflected;

            public static RadianceEstimate Absorbed
            => new RadianceEstimate();
        }

        protected struct PathState {
            public readonly Vector2 Pixel { get; init; }
            public readonly RNG Rng { get; init; }
            public RgbColor Throughput { get; set; }
            public uint Depth { get; set; }
            public SurfacePoint? PreviousHit { get; set; }
            public float PreviousPdf { get; set; }
        }

        public override void Render(Scene scene) {
            this.scene = scene;

            OnPrepareRender();

            if (RenderTechniquePyramid) {
                techPyramidRaw = new TechPyramid(scene.FrameBuffer.Width, scene.FrameBuffer.Height,
                    (int)MinDepth, (int)MaxDepth, false, false, false);
                techPyramidWeighted = new TechPyramid(scene.FrameBuffer.Width, scene.FrameBuffer.Height,
                    (int)MinDepth, (int)MaxDepth, false, false, false);
            }

            // Add custom frame buffer layers
            denoiseBuffers = new(scene.FrameBuffer);

            SeeSharp.Common.ProgressBar progressBar = new(TotalSpp);
            RNG seedGen = new(BaseSeed);
            int[] rowSeeds = new int[scene.FrameBuffer.Height];
            for (uint sampleIndex = 0; sampleIndex < TotalSpp; ++sampleIndex) {
                var stop = Stopwatch.StartNew();
                scene.FrameBuffer.StartIteration();
                OnPreIteration(sampleIndex);

                // We have one random generator per image row. Here, we pre-compute the seeds to they
                // are deterministic. This produces lower correlation than hashing the sample index and
                // row index, at basically no cost (less than one millisecond).
                for (int i = 0; i < scene.FrameBuffer.Height; ++i) {
                    rowSeeds[i] = seedGen.NextInt(int.MinValue, int.MaxValue);
                }

                Parallel.For(0, scene.FrameBuffer.Height, row => {
                    RNG rng = new((ulong)rowSeeds[row]);
                    for (uint col = 0; col < scene.FrameBuffer.Width; ++col) {
                        RenderPixel((uint)row, col, rng);
                    }
                });

                OnPostIteration(sampleIndex);

                if (sampleIndex == TotalSpp - 1)
                    denoiseBuffers.Denoise();

                scene.FrameBuffer.EndIteration();
                progressBar.ReportDone(1, stop.Elapsed.TotalSeconds);
            }

            if (RenderTechniquePyramid) {
                string pathRaw = System.IO.Path.Join(scene.FrameBuffer.Basename, "techs-raw");
                techPyramidRaw.WriteToFiles(pathRaw);
                string pathWeighted = System.IO.Path.Join(scene.FrameBuffer.Basename, "techs-weighted");
                techPyramidWeighted.WriteToFiles(pathWeighted);
            }
        }

        private void RenderPixel(uint row, uint col, RNG rng) {
            // Sample a ray from the camera
            var offset = rng.NextFloat2D();
            var pixel = new Vector2(col, row) + offset;
            Ray primaryRay = scene.Camera.GenerateRay(pixel, rng).Ray;

            PathState state = new() {
                Pixel = pixel,
                Rng = rng,
                Throughput = RgbColor.White,
                Depth = 1
            };

            OnStartPath(state);
            var estimate = EstimateIncidentRadiance(primaryRay, state);
            OnFinishedPath(pixel, rng, estimate);

            scene.FrameBuffer.Splat(col, row, estimate.Outgoing);
        }

        private RadianceEstimate EstimateIncidentRadiance(Ray ray, PathState state) {
            // Trace the next ray
            if (state.Depth > MaxDepth)
                return RadianceEstimate.Absorbed;
            var hit = scene.Raytracer.Trace(ray);

            RgbColor directHitContrib = RgbColor.Black;
            float nextEventPdf = 0;

            if (!hit && state.Depth >= MinDepth) {
                (directHitContrib, nextEventPdf) = OnBackgroundHit(ray, state);
                return new RadianceEstimate {
                    Emitted = directHitContrib,
                    NextEventPdf = nextEventPdf
                };
            } else if (!hit) {
                return RadianceEstimate.Absorbed;
            }

            OnHit(ray, hit, state);

            if (state.Depth == 1) {
                var albedo = ((SurfacePoint)hit).Material.GetScatterStrength(hit);
                denoiseBuffers.LogPrimaryHit(state.Pixel, albedo, hit.ShadingNormal);
            }

            // Check if a light source was hit.
            Emitter light = scene.QueryEmitter(hit);
            if (light != null && state.Depth >= MinDepth) {
                (directHitContrib, nextEventPdf) = OnLightHit(ray, hit, state, light);
            }

            RgbColor nextEventContrib = RgbColor.Black;
            // Perform next event estimation
            if (state.Depth + 1 >= MinDepth && state.Depth < MaxDepth) {
                for (int i = 0; i < NumShadowRays; ++i) {
                    nextEventContrib += PerformBackgroundNextEvent(ray, hit, state);
                    nextEventContrib += PerformNextEventEstimation(ray, hit, state);
                }
            }

            // Terminate early if this is the last desired bounce
            if (state.Depth >= MaxDepth) {
                return new RadianceEstimate {
                    Emitted = directHitContrib,
                    NextEventPdf = nextEventPdf,
                    Reflected = nextEventContrib
                };
            }

            // Sample a direction to continue the random walk
            (var bsdfRay, float bsdfPdf, var bsdfSampleWeight) = SampleDirection(ray, hit, state);
            if (bsdfPdf == 0 || bsdfSampleWeight == RgbColor.Black)
                return new RadianceEstimate {
                    Emitted = directHitContrib,
                    NextEventPdf = nextEventPdf,
                    Reflected = nextEventContrib
                };

            // Recursively estimate the incident radiance and log the result
            state.Throughput *= bsdfSampleWeight;
            state.Depth += 1;
            state.PreviousHit = hit;
            state.PreviousPdf = bsdfPdf;
            var nested = EstimateIncidentRadiance(bsdfRay, state);

            return new RadianceEstimate {
                Emitted = directHitContrib,
                NextEventPdf = nextEventPdf,
                Reflected = nextEventContrib + nested.Outgoing * bsdfSampleWeight
            };
        }

        private (RgbColor, float) OnBackgroundHit(Ray ray, PathState state) {
            if (scene.Background == null || !EnableBsdfDI)
                return (RgbColor.Black, 0);

            float misWeight = 1.0f;
            float pdfNextEvent = 0;
            if (state.Depth > 1) {
                // Compute the balance heuristic MIS weight
                pdfNextEvent = scene.Background.DirectionPdf(ray.Direction) * NumShadowRays;
                misWeight = 1 / (1 + pdfNextEvent / state.PreviousPdf);
            }

            var emission = scene.Background.EmittedRadiance(ray.Direction);
            RegisterSample(state.Pixel, emission * state.Throughput, misWeight, state.Depth, false);
            OnHitLightResult(ray, state, misWeight, emission, true);
            return (misWeight * emission, pdfNextEvent);
        }

        private (RgbColor, float) OnLightHit(Ray ray, SurfacePoint hit, PathState state, Emitter light) {
            float misWeight = 1.0f;
            float pdfNextEvt = 0;
            if (state.Depth > 1) { // directly visible emitters are not explicitely connected
                // Compute the solid angle pdf of next event
                var jacobian = SampleWarp.SurfaceAreaToSolidAngle(state.PreviousHit.Value, hit);
                pdfNextEvt = light.PdfArea(hit) / scene.Emitters.Count * NumShadowRays / jacobian;

                // Compute power heuristic MIS weights
                float pdfRatio = pdfNextEvt / state.PreviousPdf;
                misWeight = 1 / (pdfRatio * pdfRatio + 1);

                if (!EnableBsdfDI) misWeight = 0;
            }

            var emission = light.EmittedRadiance(hit, -ray.Direction);
            RegisterSample(state.Pixel, emission * state.Throughput, misWeight, state.Depth, false);
            OnHitLightResult(ray, state, misWeight, emission, false);
            return (misWeight * emission, pdfNextEvt);
        }

        private RgbColor PerformBackgroundNextEvent(Ray ray, SurfacePoint hit, PathState state) {
            if (scene.Background == null)
                return RgbColor.Black; // There is no background

            var sample = scene.Background.SampleDirection(state.Rng.NextFloat2D());
            if (scene.Raytracer.LeavesScene(hit, sample.Direction)) {
                var bsdfTimesCosine = hit.Material.EvaluateWithCosine(
                    hit, -ray.Direction, sample.Direction, false);
                var pdfBsdf = DirectionPdf(hit, -ray.Direction, sample.Direction);

                // Prevent NaN / Inf
                if (pdfBsdf == 0 || sample.Pdf == 0)
                    return RgbColor.Black;

                // Since the densities are in solid angle unit, no need for any conversions here
                float misWeight = EnableBsdfDI ? 1 / (1.0f + pdfBsdf / (sample.Pdf * NumShadowRays)) : 1;
                var contrib = sample.Weight * bsdfTimesCosine / NumShadowRays;

                Debug.Assert(float.IsFinite(contrib.Average));
                Debug.Assert(float.IsFinite(misWeight));

                RegisterSample(state.Pixel, contrib * state.Throughput, misWeight, state.Depth + 1, true);
                OnNextEventResult(ray, hit, state, misWeight, contrib);
                return misWeight * contrib;
            }
            return RgbColor.Black;
        }

        private RgbColor PerformNextEventEstimation(Ray ray, SurfacePoint hit, PathState state) {
            if (scene.Emitters.Count == 0)
                return RgbColor.Black;

            // Select a light source
            int idx = state.Rng.NextInt(0, scene.Emitters.Count);
            var light = scene.Emitters[idx];
            float lightSelectProb = 1.0f / scene.Emitters.Count;

            // Sample a point on the light source
            var lightSample = light.SampleArea(state.Rng.NextFloat2D());
            Vector3 lightToSurface = hit.Position - lightSample.Point.Position;

            if (!scene.Raytracer.IsOccluded(hit, lightSample.Point)) {
                var emission = light.EmittedRadiance(lightSample.Point, lightToSurface);

                // Compute the jacobian for surface area -> solid angle
                // (Inverse of the jacobian for solid angle pdf -> surface area pdf)
                float jacobian = SampleWarp.SurfaceAreaToSolidAngle(hit, lightSample.Point);
                var bsdfCos = hit.Material.EvaluateWithCosine(hit, -ray.Direction, -lightToSurface, false);

                // Compute surface area PDFs
                float pdfNextEvt = lightSample.Pdf * lightSelectProb * NumShadowRays;
                float pdfBsdfSolidAngle = DirectionPdf(hit, -ray.Direction, -lightToSurface);
                float pdfBsdf = pdfBsdfSolidAngle * jacobian;

                // Avoid Inf / NaN
                if (pdfBsdf == 0 || jacobian == 0)
                    return RgbColor.Black;

                // Compute the resulting power heuristic weights
                float pdfRatio = pdfBsdf / pdfNextEvt;
                float misWeight = EnableBsdfDI ? 1.0f / (pdfRatio * pdfRatio + 1) : 1;

                // Compute the final sample weight, account for the change of variables from light source area
                // to the hemisphere about the shading point.
                var pdf = lightSample.Pdf / jacobian * lightSelectProb * NumShadowRays;
                RegisterSample(state.Pixel, emission / pdf * bsdfCos * state.Throughput, misWeight,
                    state.Depth + 1, true);
                OnNextEventResult(ray, hit, state, misWeight, emission / pdf * bsdfCos);
                return misWeight * emission / pdf * bsdfCos;
            }
            return RgbColor.Black;
        }

        protected virtual float DirectionPdf(SurfacePoint hit, Vector3 outDir, Vector3 sampledDir)
        => hit.Material.Pdf(hit, outDir, sampledDir, false).Item1;

        protected virtual (Ray, float, RgbColor) SampleDirection(Ray ray, SurfacePoint hit, PathState state) {
            var primary = state.Rng.NextFloat2D();
            var bsdfSample = hit.Material.Sample(hit, -ray.Direction, false, primary);
            var bsdfRay = scene.Raytracer.SpawnRay(hit, bsdfSample.direction);
            return (bsdfRay, bsdfSample.pdf, bsdfSample.weight);
        }
    }

}