using System;
using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>공유 HLSL 생성기에 전달하는 형태 종류입니다. GPU 정수 값과 동일합니다.</summary>
    public enum CloudHybridGeometryKind
    {
        Ocean = 0,
        Sky = 1,
        Ribbon = 2,
        Style = 3
    }

    /// <summary>프로필이 제공하는 보정된 불변 형태 입력입니다. 날씨·바람·색은 포함하지 않습니다.</summary>
    [Serializable]
    public struct CloudHybridGeometry
    {
        public CloudHybridGeometryKind kind;

        public Vector4 placement;

        public Vector4 bounds;

        public Vector4 shape;

        public Vector4 detail;

        public Vector4 lighting;

        public Vector4 edge;

        /// <summary>실제 메시 생성에 쓰는 큰 형태 입력만 캐시 키에 포함합니다.</summary>
        public int GetShapeHash()
        {
            return HashCode.Combine(kind, placement, bounds, shape, detail, edge);
        }
    }
}
