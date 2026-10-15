namespace ClouDream.LostSkies
{
    /// <summary>공통 비행·GPU 측정을 재사용하며 OFF와 A/B 비용 분리 단계를 선택합니다.</summary>
    public sealed class CloudABBenchmark : CloudHybridBenchmark
    {
        private bool lightingBenchmark;

        private CloudHybridQualityProfile savedQuality;

        private bool savedWorldNormal;

        private bool savedDisplacedNormal;

        private bool savedMacroLighting;

        private bool savedDither;

        private bool savedSegments;

        protected override string BenchmarkArgument
        {
            get
            {
                if (lightingBenchmark)
                {
                    return "-cloudLightingBenchmark";
                }
                return "-cloudABBenchmark";
            }
        }

        protected override int RunCount
        {
            get
            {
                if (lightingBenchmark)
                {
                    return 6 * repeats;
                }
                return 7 * repeats;
            }
        }

        /// <summary>기본 반복 시간은 12초이며 임의 프레임 제한 없이 같은 경로를 재생합니다.</summary>
        protected override void Start()
        {
            durationSeconds = 12f;
            lightingBenchmark = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-cloudLightingBenchmark") >= 0;
            if (lightingBenchmark)
            {
                GetComponent<CloudHybridSwitch>().Connect();
                savedQuality = GetComponent<CloudHybridSwitch>().Hybrid.quality;
                savedWorldNormal = savedQuality.worldSpaceVolumeLighting;
                savedDisplacedNormal = savedQuality.displacementAwareNormals;
                savedMacroLighting = savedQuality.macroLighting;
                savedDither = savedQuality.lobeSilhouetteDither;
                savedSegments = savedQuality.fixedLobeSegments;
            }
            base.Start();
        }

        /// <summary>7개 표시 단계를 정순·역순으로 실행합니다. A 고정 단계는 준비 후 생성을 멈춥니다.</summary>
        protected override void ConfigureRun(int index, out CloudComparisonVariant variant,
            out CloudComparisonView view, out string mode)
        {
            if (lightingBenchmark)
            {
                ConfigureLightingRun(index, out variant, out view, out mode);
                return;
            }
            int selection = index % 7;
            if ((index / 7) % 2 == 1)
            {
                selection = 6 - selection;
            }
            variant = CloudComparisonVariant.HybridA;
            view = CloudComparisonView.Full;
            mode = "A-Full";
            if (selection == 0)
            {
                variant = CloudComparisonVariant.SurfaceB;
                view = CloudComparisonView.Off;
                mode = "Off";
            }
            else if (selection == 1)
            {
                view = CloudComparisonView.SurfaceOnly;
                mode = "A-Surface";
            }
            else if (selection == 2)
            {
                view = CloudComparisonView.SurfaceAndVolume;
                mode = "A-Local-Frozen";
            }
            else if (selection == 4)
            {
                variant = CloudComparisonVariant.SurfaceB;
                view = CloudComparisonView.SurfaceOnly;
                mode = "B-Surface";
            }
            else if (selection == 5)
            {
                variant = CloudComparisonVariant.SurfaceB;
                mode = "B-Full";
            }
            else if (selection == 6)
            {
                view = CloudComparisonView.Reference;
                mode = "Reference";
            }
        }

        /// <summary>원본·법선 수정만·전체 안정화를 같은 비행에서 정순/역순으로 분리 측정합니다.</summary>
        private void ConfigureLightingRun(int index, out CloudComparisonVariant variant,
            out CloudComparisonView view, out string mode)
        {
            int selection = index % 6;
            if ((index / 6) % 2 == 1)
            {
                selection = 5 - selection;
            }
            variant = CloudComparisonVariant.HybridA;
            string prefix = "A-";
            if (selection >= 3)
            {
                variant = CloudComparisonVariant.SurfaceB;
                prefix = "B-";
            }
            int stage = selection % 3;
            savedQuality.SetStabilityPreset(stage == 2);
            mode = prefix + "Legacy";
            if (stage == 1)
            {
                savedQuality.worldSpaceVolumeLighting = true;
                savedQuality.displacementAwareNormals = true;
                mode = prefix + "Normals";
            }
            else if (stage == 2)
            {
                mode = prefix + "Stable";
            }
            view = CloudComparisonView.Full;
        }

        /// <summary>CLI 종료와 Play 중단에서 진단이 바꾼 품질 입력까지 복구합니다.</summary>
        protected override void Restore()
        {
            base.Restore();
            if (savedQuality != null)
            {
                savedQuality.worldSpaceVolumeLighting = savedWorldNormal;
                savedQuality.displacementAwareNormals = savedDisplacedNormal;
                savedQuality.macroLighting = savedMacroLighting;
                savedQuality.lobeSilhouetteDither = savedDither;
                savedQuality.fixedLobeSegments = savedSegments;
            }
        }
    }
}
