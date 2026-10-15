namespace ClouDream.LostSkies
{
    /// <summary>공통 비행·GPU 측정을 재사용하며 OFF와 A/B 비용 분리 단계를 선택합니다.</summary>
    public sealed class CloudABBenchmark : CloudHybridBenchmark
    {
        protected override string BenchmarkArgument { get { return "-cloudABBenchmark"; } }

        protected override int RunCount { get { return 7 * repeats; } }

        /// <summary>기본 반복 시간은 12초이며 임의 프레임 제한 없이 같은 경로를 재생합니다.</summary>
        protected override void Start()
        {
            durationSeconds = 12f;
            base.Start();
        }

        /// <summary>7개 표시 단계를 정순·역순으로 실행합니다. A 고정 단계는 준비 후 생성을 멈춥니다.</summary>
        protected override void ConfigureRun(int index, out CloudComparisonVariant variant,
            out CloudComparisonView view, out string mode)
        {
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
    }
}
