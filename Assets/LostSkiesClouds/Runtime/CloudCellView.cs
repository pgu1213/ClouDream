using System.Collections.Generic;
using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>구름 생성식을 복제하지 않고 두 표현의 동일한 절대 셀 경계를 준비합니다.</summary>
    internal sealed class CloudCellView
    {
        private readonly CloudResidencyPrediction prediction = new CloudResidencyPrediction();

        public readonly List<CloudHybridMeshCache.Candidate> candidates = new List<CloudHybridMeshCache.Candidate>();

        /// <summary>같은 높이·수평 배치와 예측 영역에서 표시 후보를 고릅니다.</summary>
        public void Update(CloudHybridFrame frame, CloudHybridQualityProfile quality, Camera camera)
        {
            candidates.Clear();
            prediction.Update(frame.cameraPosition, camera.transform.forward);
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            CollectKind(0, 8192f, frame.ocean.bounds.x, frame.ocean.bounds.x + frame.ocean.bounds.y * 0.48f
                + frame.style.shape.y + Mathf.Max(3200f, frame.style.shape.x * 0.22f) * 0.54f
                + frame.style.shape.z + frame.style.detail.y + 800f, frame, quality, planes);
            CollectKind(1, frame.sky.placement.x, frame.sky.bounds.x - frame.sky.bounds.w - 600f,
                frame.sky.bounds.y + frame.sky.bounds.w + 600f, frame, quality, planes);
            CollectKind(2, 14000f, 3200f, 11600f, frame, quality, planes);
            candidates.Sort(new NearestFirst());
        }

        /// <summary>보수적 AABB와 비대칭 준비 범위를 만들고 실제 시야만 표시합니다.</summary>
        private void CollectKind(int kind, float spacing, float bottom, float top,
            CloudHybridFrame frame, CloudHybridQualityProfile quality, Plane[] planes)
        {
            float residencyDistance = quality.viewDistance + 16000f;
            int radius = Mathf.CeilToInt(residencyDistance / spacing);
            int centerX = Mathf.FloorToInt(frame.cameraPosition.x / spacing);
            int centerZ = Mathf.FloorToInt(frame.cameraPosition.z / spacing);
            for (int z = centerZ - radius; z <= centerZ + radius; z++)
            {
                for (int x = centerX - radius; x <= centerX + radius; x++)
                {
                    Bounds bounds = new Bounds(new Vector3((x + 0.5f) * spacing,
                        (bottom + top) * 0.5f, (z + 0.5f) * spacing), new Vector3(spacing, top - bottom, spacing));
                    float distance = Vector3.Distance(frame.cameraPosition, bounds.ClosestPoint(frame.cameraPosition));
                    Bounds sceneBounds = bounds;
                    sceneBounds.center -= frame.origin;
                    float margin = prediction.Margin(frame.cameraPosition, bounds.ClosestPoint(frame.cameraPosition), quality);
                    if (distance > quality.viewDistance + margin)
                    {
                        continue;
                    }

                    CloudHybridMeshCache.Candidate candidate = new CloudHybridMeshCache.Candidate();
                    candidate.key = new Vector3Int(x, z, kind);
                    candidate.bounds = bounds;
                    candidate.distance = distance;
                    candidate.grid = quality.GridForDistance(distance);
                    candidate.visible = distance <= quality.viewDistance
                        && GeometryUtility.TestPlanesAABB(planes, sceneBounds);
                    candidates.Add(candidate);
                }
            }
        }

        /// <summary>가까운 표시 셀부터 생성합니다.</summary>
        private sealed class NearestFirst : IComparer<CloudHybridMeshCache.Candidate>
        {
            /// <summary>동일 거리에서도 셀 ID로 결정적인 순서를 유지합니다.</summary>
            public int Compare(CloudHybridMeshCache.Candidate first, CloudHybridMeshCache.Candidate second)
            {
                int order = first.distance.CompareTo(second.distance);
                if (order == 0)
                {
                    order = first.key.z.CompareTo(second.key.z);
                }

                if (order == 0)
                {
                    order = first.key.x.CompareTo(second.key.x);
                }

                if (order == 0)
                {
                    order = first.key.y.CompareTo(second.key.y);
                }

                return order;
            }
        }

    }
}
