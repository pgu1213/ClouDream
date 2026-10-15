using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>하이브리드의 화면 품질과 스트리밍 예산을 한 에셋에서 관리합니다.</summary>
    [CreateAssetMenu(menuName = "ClouDream/Clouds/Hybrid Quality")]
    public sealed class CloudHybridQualityProfile : ScriptableObject
    {
        [Header("RTX 4080 / FHD / 144 FPS")]
        [Range(0.25f, 1f)] public float resolutionScale = 0.5f;

        [Min(100f)] public float volumeNear = 1000f;

        [Min(200f)] public float volumeFar = 2000f;

        [Range(16, 96)] public int primarySamples = 48;

        [Range(0, 4)] public int lightSamples = 2;

        [Header("절대 월드 메시 LOD")]
        public int nearGrid = 64;

        public int middleGrid = 32;

        public int farGrid = 16;

        [Min(2000f)] public float middleDistance = 6000f;

        [Min(4000f)] public float farDistance = 24000f;

        [Min(8000f)] public float viewDistance = 42000f;

        [Min(0f)] public float prefetchDistance = 8000f;

        [Header("생성·메모리 예산")]
        [Range(16, 256)] public int memoryMiB = 128;

        [Range(1, 16)] public int buildSlicesPerFrame = 4;

        [Range(1, 4)] public int buildsInFlight = 1;

        [Range(16, 512)] public int maximumCells = 384;

        [Header("검증 합격 기준 (ms)")]
        public float frameP95 = 6.944444f;

        public float cloudMean = 2f;

        public float cloudP95 = 3f;

        /// <summary>불완전한 외부 입력에서도 전환과 LOD 범위를 유효하게 유지합니다.</summary>
        public void Sanitize()
        {
            resolutionScale = Mathf.Clamp(resolutionScale, 0.25f, 1f);
            volumeNear = Mathf.Max(100f, volumeNear);
            volumeFar = Mathf.Max(volumeNear + 100f, volumeFar);
            primarySamples = Mathf.Clamp(primarySamples, 16, 96);
            lightSamples = Mathf.Clamp(lightSamples, 0, 4);
            nearGrid = ValidGrid(nearGrid);
            middleGrid = Mathf.Min(nearGrid, ValidGrid(middleGrid));
            farGrid = Mathf.Min(middleGrid, ValidGrid(farGrid));
            middleDistance = Mathf.Max(volumeFar, middleDistance);
            farDistance = Mathf.Max(middleDistance + 100f, farDistance);
            viewDistance = Mathf.Max(farDistance + 100f, viewDistance);
            prefetchDistance = Mathf.Clamp(prefetchDistance, 0f, 16000f);
            memoryMiB = Mathf.Clamp(memoryMiB, 16, 256);
            buildSlicesPerFrame = Mathf.Clamp(buildSlicesPerFrame, 1, 16);
            buildsInFlight = Mathf.Clamp(buildsInFlight, 1, 4);
            maximumCells = Mathf.Clamp(maximumCells, 16, 512);
        }

        /// <summary>대각선 방향에 상관없이 보수적인 표면 경계 거리를 기준으로 격자를 고릅니다.</summary>
        public int GridForDistance(float distance)
        {
            if (distance < middleDistance)
            {
                return nearGrid;
            }

            if (distance < farDistance)
            {
                return middleGrid;
            }

            return farGrid;
        }

        /// <summary>GPU 격자는 검증 가능한 16/32/64 단계만 사용합니다.</summary>
        private static int ValidGrid(int value)
        {
            if (value >= 64)
            {
                return 64;
            }

            if (value >= 32)
            {
                return 32;
            }

            return 16;
        }
    }
}
