using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>같은 구름을 회전, 관통, 운해 및 층운 비행 경로에서 촬영하고 실제 카메라 좌표를 기록합니다.</summary>
    public static class CloudStyleCapture
    {
        public const int TotalFrames = 120;
        public const int CaptureWidth = 960;
        public const int CaptureHeight = 540;
        public const float CaptureFieldOfView = 68f;

        /// <summary>한 프레임에서 사용한 Unity 월드 좌표와 밀도장 좌표를 함께 기록합니다.</summary>
        [Serializable]
        public sealed class CameraPose
        {
            public int frame;
            public Vector3 worldPosition;
            public Quaternion worldRotation;
            public Vector3 eulerAngles;

            public Vector3 densityPosition;
        }

        /// <summary>상층 구름의 선택 근거와 전체 경로를 보관합니다. 일부 프레임만 촬영해도 경로는 동일합니다.</summary>
        [Serializable]
        public sealed class PathDescription
        {
            public string pathName;
            public int totalFrames = TotalFrames;
            public int width = CaptureWidth;
            public int height = CaptureHeight;
            public float fieldOfView = CaptureFieldOfView;

            public int shapeMode;
            public string shapeVersion;

            public int seed;
            public float spacing;
            public Vector2Int selectedCell;
            public Vector3 cloudCenter;
            public Vector3 cloudDensityCenter;
            public Vector3 cloudRadii;
            public float cloudRotationRadians;

            // 기존 층운은 원래 slope, V2는 중심에서 높이 변화율의 음수입니다.
            public float cloudSlope;

            // V2의 곡률 위상과 셀 기준 고도에서 실제 관통 중심까지의 높이 차이를 기록합니다.
            public float ribbonCurvaturePhase;
            public float ribbonCenterCurveOffset;

            public Vector3 samplingOffset;

            public CameraPose[] poses;
        }

        /// <summary>경로와 촬영 시각을 JSON에 함께 저장합니다. poses는 촬영 예정 프레임도 포함합니다.</summary>
        [Serializable]
        private sealed class CaptureManifest
        {
            public string capturedAtUtc;
            public float requestedHour;
            public float appliedHour;
            public bool wasPlaying;
            public int batchStart;
            public int batchCount;
            public string poseMeaning = "All 120 planned camera poses; PNG files identify the frames captured so far.";

            public PathDescription path;
        }

        /// <summary>120장의 연속 경로에서 지정 구간을 촬영합니다. 에디터 응답성을 위해 호출당 20장을 권장합니다.</summary>
        public static string CapturePath(string pathName, int startFrame, int count, float hour, string outputRoot = "StyleMotion-Final")
        {
            if (startFrame < 0 || startFrame >= TotalFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(startFrame));
            }

            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (string.IsNullOrWhiteSpace(outputRoot) || outputRoot.Contains("/") || outputRoot.Contains("\\") || outputRoot.Contains(".."))
            {
                throw new ArgumentException("촬영 폴더는 Screenshots 안의 단일 폴더 이름이어야 합니다.");
            }

            Camera camera = RequireCamera();
            LostSkiesCloudPass pass = RequirePass();
            CloudTimeOfDayController clock = RequireClock(pass);
            PathDescription description = DescribePath(pathName);
            int endFrame = Mathf.Min(TotalFrames, startFrame + count);

            string hourName = hour.ToString("0.##", CultureInfo.InvariantCulture);
            string directory = Path.GetFullPath(Path.Combine("Screenshots", outputRoot, description.pathName + "-" + hourName));
            Directory.CreateDirectory(directory);

            Vector3 savedPosition = camera.transform.position;
            Quaternion savedRotation = camera.transform.rotation;
            float savedFieldOfView = camera.fieldOfView;
            float savedAspect = camera.aspect;
            RenderTexture savedTarget = camera.targetTexture;
            RenderTexture savedActive = RenderTexture.active;

            float savedHour = clock.TimeOfDay;
            bool savedAutoAdvance = clock.autoAdvance;
            float savedWindSpeed = pass.windSpeed;

            RenderTexture target = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D pixels = null;
            try
            {
                target.name = "Cloud style path capture";
                target.Create();
                pixels = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);

                // 시간 차이 없이 시점 차이만 비교하도록 Play 중에도 바람과 태양의 진행을 멈춥니다.
                pass.windSpeed = 0f;
                clock.autoAdvance = false;
                clock.SetTime(hour);

                camera.targetTexture = target;
                camera.fieldOfView = CaptureFieldOfView;
                camera.aspect = (float)CaptureWidth / CaptureHeight;
                for (int frame = startFrame; frame < endFrame; frame++)
                {
                    CameraPose pose = description.poses[frame];
                    camera.transform.SetPositionAndRotation(pose.worldPosition, pose.worldRotation);
                    CaptureFrame(camera, target, pixels, Path.Combine(directory, "frame-" + frame.ToString("000", CultureInfo.InvariantCulture) + ".png"));
                }

                CaptureManifest manifest = new CaptureManifest();
                manifest.capturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                manifest.requestedHour = hour;
                manifest.appliedHour = clock.TimeOfDay;
                manifest.wasPlaying = Application.isPlaying;
                manifest.batchStart = startFrame;
                manifest.batchCount = endFrame - startFrame;
                manifest.path = description;
                File.WriteAllText(Path.Combine(directory, "poses.json"), JsonUtility.ToJson(manifest, true));
            }
            finally
            {
                camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
                camera.fieldOfView = savedFieldOfView;
                camera.aspect = savedAspect;
                camera.targetTexture = savedTarget;
                RenderTexture.active = savedActive;

                pass.windSpeed = savedWindSpeed;
                clock.SetTime(savedHour);
                clock.autoAdvance = savedAutoAdvance;

                if (pixels != null)
                {
                    UnityEngine.Object.DestroyImmediate(pixels);
                }

                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }

            return directory;
        }

        /// <summary>현재 카메라에 가까운 생성 셀을 선택하고 촬영 없이 120프레임의 위치와 방향을 반환합니다.</summary>
        public static PathDescription DescribePath(string pathName)
        {
            string normalizedName = NormalizePathName(pathName);
            Camera camera = RequireCamera();
            LostSkiesCloudPass pass = RequirePass();
            CloudSkyProfile sky = pass.skyProfile;
            if (sky == null || pass.useExtractedValues)
            {
                throw new InvalidOperationException("상층 구름을 생성하는 CloudSkyProfile이 필요합니다.");
            }

            Vector3 offset = Vector3.zero;
            if (pass.worldOrigin != null)
            {
                offset = pass.worldOrigin.SamplingOffset;
            }

            PathDescription description;
            if (normalizedName == "ribbon")
            {
                if (pass.styleProfile == null)
                {
                    throw new InvalidOperationException("층운 촬영에는 LayeredBillows 또는 ConceptV2 형태의 CloudStyleProfile이 필요합니다.");
                }

                if (pass.styleProfile.method == CloudStyleProfile.ShapeMethod.ConceptV2)
                {
                    description = SelectConceptRibbon(camera.transform.position + offset, sky, pass.styleProfile, offset);
                }
                else if (pass.styleProfile.method == CloudStyleProfile.ShapeMethod.LayeredBillows)
                {
                    description = SelectRibbon(camera.transform.position + offset, sky.seed, offset);
                }
                else
                {
                    throw new InvalidOperationException("층운 촬영에는 LayeredBillows 또는 ConceptV2 형태의 CloudStyleProfile이 필요합니다.");
                }
            }
            else
            {
                if (sky.occupiedCells <= 0f)
                {
                    throw new InvalidOperationException("상층 구름을 생성하는 CloudSkyProfile이 필요합니다.");
                }

                description = SelectCloud(normalizedName, camera.transform.position + offset, sky, offset);
            }

            description.shapeMode = 0;
            description.shapeVersion = "LegacyVolumetric";
            if (pass.styleProfile != null)
            {
                description.shapeMode = (int)pass.styleProfile.method;
                description.shapeVersion = pass.styleProfile.method.ToString();
            }

            description.poses = BuildPoses(description, camera, pass.oceanProfile);
            return description;
        }

        /// <summary>지원하는 경로 이름만 허용하여 잘못된 출력 경로 또는 오타를 즉시 알립니다.</summary>
        private static string NormalizePathName(string pathName)
        {
            if (string.IsNullOrWhiteSpace(pathName))
            {
                throw new ArgumentException("orbit, traverse, sea, ribbon 중 경로 이름을 지정해야 합니다.", nameof(pathName));
            }

            string normalized = pathName.Trim().ToLowerInvariant();
            if (normalized != "orbit" && normalized != "traverse" && normalized != "sea" && normalized != "ribbon")
            {
                throw new ArgumentException("지원하는 촬영 경로는 orbit, traverse, sea, ribbon입니다.", nameof(pathName));
            }

            return normalized;
        }

        /// <summary>실제 게임 카메라를 가져오고 연결이 없으면 촬영을 중단합니다.</summary>
        private static Camera RequireCamera()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                throw new InvalidOperationException("MainCamera 태그가 지정된 게임 카메라가 필요합니다.");
            }

            return camera;
        }

        /// <summary>활성 볼륨에 연결된 실제 구름 패스를 찾아 프로필과 풍속을 공유합니다.</summary>
        private static LostSkiesCloudPass RequirePass()
        {
            CustomPassVolume[] volumes = UnityEngine.Object.FindObjectsByType<CustomPassVolume>();
            foreach (CustomPassVolume volume in volumes)
            {
                if (!volume.isActiveAndEnabled)
                {
                    continue;
                }

                foreach (CustomPass customPass in volume.customPasses)
                {
                    LostSkiesCloudPass cloudPass = customPass as LostSkiesCloudPass;
                    if (cloudPass != null && cloudPass.enabled)
                    {
                        return cloudPass;
                    }
                }
            }

            throw new InvalidOperationException("활성 LostSkiesCloudPass를 찾지 못했습니다.");
        }

        /// <summary>구름 패스의 공유 시간 공급자를 찾아 같은 태양과 하늘을 촬영합니다.</summary>
        private static CloudTimeOfDayController RequireClock(LostSkiesCloudPass pass)
        {
            CloudTimeOfDayController clock = pass.environmentSource as CloudTimeOfDayController;
            if (clock == null || !clock.isActiveAndEnabled)
            {
                throw new InvalidOperationException("구름 패스에 활성 CloudTimeOfDayController가 연결되어 있어야 합니다.");
            }

            return clock;
        }

        /// <summary>컴퓨트 셰이더와 같은 셀 난수와 반경 제한으로 가까운 상층 구름의 경계를 구합니다.</summary>
        private static PathDescription SelectCloud(string pathName, Vector3 cameraPosition, CloudSkyProfile sky, Vector3 offset)
        {
            float spacing = Mathf.Max(4000f, sky.spacing);
            Vector2 heights = CloudSkyProfile.OrderedRange(sky.altitude, -100000f);
            Vector2 widths = CloudSkyProfile.OrderedRange(sky.horizontalRadius, 100f);
            Vector2 depths = CloudSkyProfile.OrderedRange(sky.verticalRadius, 100f);
            widths.x = Mathf.Min(widths.x, spacing * 0.38f);
            widths.y = Mathf.Min(widths.y, spacing * 0.38f);

            int cameraCellX = Mathf.FloorToInt(cameraPosition.x / spacing);
            int cameraCellZ = Mathf.FloorToInt(cameraPosition.z / spacing);
            float closestDistance = float.PositiveInfinity;
            PathDescription selected = null;
            for (int z = cameraCellZ - 8; z <= cameraCellZ + 8; z++)
            {
                for (int x = cameraCellX - 8; x <= cameraCellX + 8; x++)
                {
                    if (CellRandom(x, z, sky.seed, 0u) >= Mathf.Clamp01(sky.occupiedCells))
                    {
                        continue;
                    }

                    float jitterX = (CellRandom(x, z, sky.seed, 1u) - 0.5f) * 0.12f;
                    float jitterZ = (CellRandom(x, z, sky.seed, 2u) - 0.5f) * 0.12f;
                    Vector3 center = new Vector3((x + 0.5f + jitterX) * spacing,
                        Mathf.Lerp(heights.x, heights.y, CellRandom(x, z, sky.seed, 3u)),
                        (z + 0.5f + jitterZ) * spacing);
                    float distance = (center - cameraPosition).sqrMagnitude;
                    if (distance >= closestDistance)
                    {
                        continue;
                    }

                    closestDistance = distance;
                    selected = new PathDescription();
                    selected.pathName = pathName;
                    selected.seed = sky.seed;
                    selected.spacing = spacing;
                    selected.selectedCell = new Vector2Int(x, z);
                    selected.cloudCenter = center - offset;
                    selected.cloudDensityCenter = center;
                    selected.cloudRadii = new Vector3(Mathf.Lerp(widths.x, widths.y, CellRandom(x, z, sky.seed, 4u)),
                        Mathf.Lerp(depths.x, depths.y, CellRandom(x, z, sky.seed, 6u)),
                        Mathf.Lerp(widths.x, widths.y, CellRandom(x, z, sky.seed, 5u)));
                    selected.cloudRotationRadians = CellRandom(x, z, sky.seed, 7u) * Mathf.PI * 2f;
                    selected.samplingOffset = offset;
                }
            }

            if (selected == null)
            {
                throw new InvalidOperationException("카메라 주변 17×17 셀에서 상층 구름을 찾지 못했습니다.");
            }

            return selected;
        }

        /// <summary>셰이더의 층운 전용 셀과 salt 140~145를 재현하여 가장 가까운 실제 층운을 선택합니다.</summary>
        private static PathDescription SelectRibbon(Vector3 cameraPosition, int seed, Vector3 offset)
        {
            // CloudSculpting.hlsl의 CloudRibbonDensity와 공유하는 형상 계약입니다.
            const float spacing = 14000f;
            int cameraCellX = Mathf.FloorToInt(cameraPosition.x / spacing);
            int cameraCellZ = Mathf.FloorToInt(cameraPosition.z / spacing);
            float closestDistance = float.PositiveInfinity;
            PathDescription selected = null;

            for (int z = cameraCellZ - 8; z <= cameraCellZ + 8; z++)
            {
                for (int x = cameraCellX - 8; x <= cameraCellX + 8; x++)
                {
                    if (CellRandom(x, z, seed, 140u) > 0.65f)
                    {
                        continue;
                    }

                    float centerHeight = Mathf.Lerp(4100f, 10200f, CellRandom(x, z, seed, 141u));
                    Vector3 center = new Vector3((x + 0.5f) * spacing, centerHeight, (z + 0.5f) * spacing);
                    float distance = (center - cameraPosition).sqrMagnitude;
                    if (distance >= closestDistance)
                    {
                        continue;
                    }

                    closestDistance = distance;
                    selected = new PathDescription();
                    selected.pathName = "ribbon";
                    selected.seed = seed;
                    selected.spacing = spacing;
                    selected.selectedCell = new Vector2Int(x, z);
                    selected.cloudCenter = center - offset;
                    selected.cloudDensityCenter = center;

                    // x는 장축 반경, y는 밀도장의 반두께, z는 짧은 축 반경입니다.
                    selected.cloudRadii = new Vector3(
                        Mathf.Lerp(3300f, 5100f, CellRandom(x, z, seed, 143u)),
                        Mathf.Lerp(130f, 260f, CellRandom(x, z, seed, 144u)),
                        1200f);
                    selected.cloudRotationRadians = CellRandom(x, z, seed, 142u) * Mathf.PI * 2f;
                    selected.cloudSlope = (CellRandom(x, z, seed, 145u) - 0.5f) * 0.10f;
                    selected.samplingOffset = offset;
                }
            }

            if (selected == null)
            {
                throw new InvalidOperationException("카메라 주변 17×17 셀에서 층운을 찾지 못했습니다.");
            }

            return selected;
        }

        /// <summary>V2 층운의 셀, 고도, 지역 방향과 중심 곡률을 재현하여 실제 두께를 관통할 위치를 선택합니다.</summary>
        private static PathDescription SelectConceptRibbon(Vector3 cameraPosition, CloudSkyProfile sky,
            CloudStyleProfile style, Vector3 offset)
        {
            if (sky.occupiedCells <= 0f || sky.density <= 0f)
            {
                throw new InvalidOperationException("V2 층운 촬영에는 점유율과 밀도가 0보다 큰 상층 프로필이 필요합니다.");
            }

            // CloudConceptShapes.hlsl의 ConceptSampleRibbon과 같은 형상 계약입니다.
            const float spacing = 14000f;
            float occupancy = Mathf.Clamp01(Mathf.Clamp01(sky.occupiedCells) * (0.65f / 0.72f));
            int cameraCellX = Mathf.FloorToInt(cameraPosition.x / spacing);
            int cameraCellZ = Mathf.FloorToInt(cameraPosition.z / spacing);
            float closestDistance = float.PositiveInfinity;
            PathDescription selected = null;

            for (int z = cameraCellZ - 8; z <= cameraCellZ + 8; z++)
            {
                for (int x = cameraCellX - 8; x <= cameraCellX + 8; x++)
                {
                    if (CellRandom(x, z, sky.seed, 140u) >= occupancy)
                    {
                        continue;
                    }

                    float baseHeight = Mathf.Lerp(4500f, 9900f, CellRandom(x, z, sky.seed, 141u));
                    float lengthRadius = Mathf.Lerp(3500f, 4900f, CellRandom(x, z, sky.seed, 143u));
                    float thickness = Mathf.Lerp(180f, 300f, CellRandom(x, z, sky.seed, 144u));
                    float phase = CellRandom(x, z, sky.seed, 147u) * Mathf.PI * 2f;

                    // 장축 위치 along=0에서 HLSL의 centerCurve를 정확히 평가합니다.
                    float curveAtCenter = Mathf.Sin(phase);
                    float centerCurve = curveAtCenter * 140f;
                    Vector3 center = new Vector3((x + 0.5f) * spacing,
                        baseHeight + centerCurve, (z + 0.5f) * spacing);
                    float distance = (center - cameraPosition).sqrMagnitude;
                    if (distance >= closestDistance)
                    {
                        continue;
                    }

                    // centerCurve의 장축 미분으로 실제 중심 접평면과 관통 법선을 정합니다.
                    float curveSlope = (Mathf.Cos(phase) * 2.4f * 140f
                        + (CellRandom(x, z, sky.seed, 145u) - 0.5f) * 240f) / lengthRadius;
                    float width = Mathf.Lerp(750f, 1150f, CellRandom(x, z, sky.seed, 146u))
                        * (0.88f + curveAtCenter * 0.12f);

                    closestDistance = distance;
                    selected = new PathDescription();
                    selected.pathName = "ribbon";
                    selected.seed = sky.seed;
                    selected.spacing = spacing;
                    selected.selectedCell = new Vector2Int(x, z);
                    selected.cloudCenter = center - offset;
                    selected.cloudDensityCenter = center;
                    selected.cloudRadii = new Vector3(lengthRadius, thickness, width);
                    selected.cloudRotationRadians = style.regionalFlowDegrees * Mathf.Deg2Rad
                        + (CellRandom(x, z, sky.seed, 142u) - 0.5f) * 0.44f;
                    selected.cloudSlope = -curveSlope;
                    selected.ribbonCurvaturePhase = phase;
                    selected.ribbonCenterCurveOffset = centerCurve;
                    selected.samplingOffset = offset;
                }
            }

            if (selected == null)
            {
                throw new InvalidOperationException("카메라 주변 17×17 셀에서 V2 층운을 찾지 못했습니다.");
            }

            return selected;
        }

        /// <summary>GPU의 CellRandom과 동일한 32비트 오버플로 연산으로 위치와 형태의 난수를 재현합니다.</summary>
        private static float CellRandom(int x, int z, int seed, uint salt)
        {
            unchecked
            {
                uint value = (uint)x * 747796405u + (uint)z * 2891336453u;
                value += (uint)seed * 277803737u + salt * 1597334677u;
                value = (value ^ (value >> 16)) * 2246822519u;
                value = (value ^ (value >> 13)) * 3266489917u;
                value ^= value >> 16;
                return (value & 0x00ffffffu) / 16777216f;
            }
        }

        /// <summary>카메라와 구름 사이 방향을 기준으로 모든 배치에서 재사용할 연속 경로를 생성합니다.</summary>
        private static CameraPose[] BuildPoses(PathDescription description, Camera camera, CloudOceanProfile ocean)
        {
            if (description.pathName == "ribbon")
            {
                return BuildRibbonPoses(description);
            }

            CameraPose[] poses = new CameraPose[TotalFrames];
            Vector3 travelDirection = description.cloudCenter - camera.transform.position;
            travelDirection.y = 0f;
            if (travelDirection.sqrMagnitude < 0.0001f)
            {
                travelDirection = Vector3.forward;
            }

            travelDirection.Normalize();
            float maximumRadius = Mathf.Max(description.cloudRadii.x, description.cloudRadii.z);
            float orbitRadius = maximumRadius * 2.35f;
            float startAngle = Mathf.Atan2(-travelDirection.x, -travelDirection.z);
            for (int frame = 0; frame < TotalFrames; frame++)
            {
                float amount = (float)frame / (TotalFrames - 1);
                Vector3 position;
                Quaternion rotation;
                if (description.pathName == "orbit")
                {
                    float angle = startAngle + amount * Mathf.PI * 2f;
                    Vector3 radial = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                    float height = description.cloudRadii.y * (0.2f + 0.25f * Mathf.Sin(amount * Mathf.PI * 2f));
                    position = description.cloudCenter + radial * orbitRadius + Vector3.up * height;
                    rotation = Quaternion.LookRotation(description.cloudCenter - position, Vector3.up);
                }
                else if (description.pathName == "traverse")
                {
                    float distance = Mathf.Lerp(-2.5f, 2.5f, amount) * maximumRadius;
                    position = description.cloudCenter + travelDirection * distance;
                    rotation = Quaternion.LookRotation(travelDirection, Vector3.up);
                }
                else
                {
                    BuildSeaPose(camera, ocean, description.samplingOffset, amount, out position, out rotation);
                }

                CameraPose pose = new CameraPose();
                pose.frame = frame;
                pose.worldPosition = position;
                pose.worldRotation = rotation;
                pose.eulerAngles = rotation.eulerAngles;
                pose.densityPosition = position + description.samplingOffset;
                poses[frame] = pose;
            }

            return poses;
        }

        /// <summary>층운을 위·옆·아래에서 한 바퀴 관찰한 뒤 중심의 실제 두께를 위에서 아래로 통과합니다.</summary>
        private static CameraPose[] BuildRibbonPoses(PathDescription description)
        {
            CameraPose[] poses = new CameraPose[TotalFrames];
            float sine = Mathf.Sin(description.cloudRotationRadians);
            float cosine = Mathf.Cos(description.cloudRotationRadians);

            // HLSL의 rotated.x와 기울어진 중심 평면을 역변환하여 카메라 경로의 기준 축을 만듭니다.
            Vector3 longAxis = new Vector3(cosine, -description.cloudSlope, -sine).normalized;
            Vector3 shortAxis = new Vector3(sine, 0f, cosine);
            Vector3 normal = Vector3.Cross(shortAxis, longAxis).normalized;
            float orbitRadius = description.cloudRadii.x * 1.7f;
            float orbitHeight = Mathf.Max(1600f, description.cloudRadii.x * 0.5f);
            float crossingHeight = Mathf.Max(800f, description.cloudRadii.y * 4f);

            Vector3 orbitStart = description.cloudCenter + shortAxis * orbitRadius + normal * orbitHeight;
            Quaternion orbitStartRotation = Quaternion.LookRotation(description.cloudCenter - orbitStart, normal);
            Vector3 crossingStart = description.cloudCenter + normal * crossingHeight;
            Quaternion crossingRotation = Quaternion.LookRotation(-normal, longAxis);

            for (int frame = 0; frame < TotalFrames; frame++)
            {
                Vector3 position;
                Quaternion rotation;

                // 0~79: 수평 방향을 360도 회전하며 층운 평면의 위와 아래를 번갈아 관찰합니다.
                if (frame <= 79)
                {
                    float angle = frame / 79f * Mathf.PI * 2f;
                    Vector3 radial = shortAxis * Mathf.Cos(angle) + longAxis * Mathf.Sin(angle);
                    position = description.cloudCenter + radial * orbitRadius + normal * (orbitHeight * Mathf.Cos(angle));
                    rotation = Quaternion.LookRotation(description.cloudCenter - position, normal);
                }
                // 80~95: 시작 시점에서 층운 중심 위로 이동하면서 수직 관통 방향을 준비합니다.
                else if (frame <= 95)
                {
                    float amount = Mathf.SmoothStep(0f, 1f, (frame - 79f) / 16f);
                    position = Vector3.Lerp(orbitStart, crossingStart, amount);
                    rotation = Quaternion.Slerp(orbitStartRotation, crossingRotation, amount);
                }
                // 96~119: 카메라를 뒤집지 않고 중심을 관통하여 앞면·내부·뒷면의 연속성을 확인합니다.
                else
                {
                    float amount = (frame - 95f) / 24f;
                    float height = Mathf.Lerp(crossingHeight, -crossingHeight, amount);
                    position = description.cloudCenter + normal * height;
                    rotation = crossingRotation;
                }

                CameraPose pose = new CameraPose();
                pose.frame = frame;
                pose.worldPosition = position;
                pose.worldRotation = rotation;
                pose.eulerAngles = rotation.eulerAngles;
                pose.densityPosition = position + description.samplingOffset;
                poses[frame] = pose;
            }

            return poses;
        }

        /// <summary>운해 상단에서 옆으로 이동한 뒤 아래로 관통하여 측면과 내부의 연속성을 확인합니다.</summary>
        private static void BuildSeaPose(Camera camera, CloudOceanProfile ocean, Vector3 offset, float amount, out Vector3 position, out Quaternion rotation)
        {
            float bottom = -1600f;
            float thickness = 2700f;
            if (ocean != null)
            {
                bottom = ocean.bottom;
                thickness = Mathf.Max(100f, ocean.thickness);
            }

            Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 start = camera.transform.position;
            start.y = bottom + thickness + 350f - offset.y;

            float lateralAmount = Mathf.Clamp01(amount * 2f);
            float descentAmount = Mathf.Clamp01((amount - 0.5f) * 2f);
            position = start + right * Mathf.Lerp(-5000f, 5000f, lateralAmount);
            position.y = Mathf.Lerp(start.y, bottom - 400f - offset.y, descentAmount);

            Vector3 direction = Vector3.Lerp(forward + Vector3.down * 0.24f,
                forward * 0.25f + Vector3.down, Mathf.SmoothStep(0f, 1f, descentAmount));
            rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        /// <summary>HDRP 하늘과 노출이 안정화된 뒤 같은 게임 카메라의 색 버퍼를 PNG로 기록합니다.</summary>
        private static void CaptureFrame(Camera camera, RenderTexture target, Texture2D pixels, string path)
        {
            for (int warmup = 0; warmup < 2; warmup++)
            {
                camera.Render();
            }

            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0f, 0f, CaptureWidth, CaptureHeight), 0, 0);
            pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
    }
}
