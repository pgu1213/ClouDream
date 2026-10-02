using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>카메라 주변 상층 셀의 불변 몸체를 GPU에 보관합니다. 범위 밖은 기존 절차식으로 평가합니다.</summary>
    internal sealed class CloudShapeCellCache : IDisposable
    {
        // HLSL 계약: 셀마다 float4 32개. 0은 성장/크기/결합 폭, 1은 Z 성장 난수,
        // 2..29는 몸체 3 + 어깨 2 + 렌더 로브 9의 (중심, 반경) 쌍입니다.
        private const int Side = 32;
        private const int RecordsPerCell = 32;

        private ComputeBuffer buffer;
        private bool valid;
        private Vector2Int origin;
        private Vector4 placement;
        private Vector4 altitude;
        private Vector4 shape;

        private readonly int[] region = new int[4];

        public int BuildCount { get; private set; }

        /// <summary>시드·크기·셀 창이 바뀔 때만 캐시를 재생성하고 모든 조회 커널에 연결합니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, int rayKernel, int probeKernel,
            CloudFormationProfile profile, Vector3 cameraPosition, bool enabled)
        {
            if (buffer == null)
            {
                buffer = new ComputeBuffer(Side * Side * RecordsPerCell, 16);
            }

            commands.SetComputeBufferParam(shader, rayKernel, "_CloudShapeCells", buffer);
            commands.SetComputeBufferParam(shader, probeKernel, "_CloudShapeCells", buffer);
            commands.SetComputeIntParam(shader, "_CloudShapeCacheEnabled", 0);
            if (!enabled || profile == null
                || !profile.TryGetSkyCellParameters(out Vector4 nextPlacement, out Vector4 nextAltitude, out Vector4 nextShape))
            {
                return;
            }

            Vector2Int nextOrigin = new Vector2Int(Mathf.FloorToInt(cameraPosition.x / nextPlacement.x) - Side / 2,
                Mathf.FloorToInt(cameraPosition.z / nextPlacement.x) - Side / 2);
            region[0] = nextOrigin.x;
            region[1] = nextOrigin.y;
            region[2] = Side;
            region[3] = RecordsPerCell;
            commands.SetComputeIntParams(shader, "_CloudShapeCacheRegion", region);
            if (!valid || origin != nextOrigin || !placement.Equals(nextPlacement)
                || !altitude.Equals(nextAltitude) || !shape.Equals(nextShape))
            {
                int buildKernel = shader.FindKernel("BuildShapeCells");
                commands.SetComputeBufferParam(shader, buildKernel, "_CloudShapeCells", buffer);
                commands.DispatchCompute(shader, buildKernel, Side / 8, Side / 8, 1);
                origin = nextOrigin;
                placement = nextPlacement;
                altitude = nextAltitude;
                shape = nextShape;
                valid = true;
                BuildCount++;
            }

            commands.SetComputeIntParam(shader, "_CloudShapeCacheEnabled", 1);
        }

        /// <summary>직접 실행하는 진단 프로브에도 같은 버퍼를 연결합니다.</summary>
        public void BindProbe(ComputeShader shader, int kernel)
        {
            if (buffer != null)
            {
                shader.SetBuffer(kernel, "_CloudShapeCells", buffer);
            }
        }

        /// <summary>조명 체적 생성도 동일한 상층 몸체 버퍼를 읽도록 명령 버퍼에 연결합니다.</summary>
        public void BindKernel(CommandBuffer commands, ComputeShader shader, int kernel)
        {
            if (buffer != null)
            {
                commands.SetComputeBufferParam(shader, kernel, "_CloudShapeCells", buffer);
            }
        }

        /// <summary>렌더러 수명이 끝나면 캐시 GPU 메모리를 반환합니다.</summary>
        public void Dispose()
        {
            if (buffer != null)
            {
                buffer.Release();
                buffer = null;
            }

            valid = false;
        }
    }
}
