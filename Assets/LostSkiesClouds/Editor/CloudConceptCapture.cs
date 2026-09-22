using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>컨셉 A/B 실험을 고정된 월드 시점에서 촬영하고 재현에 필요한 입력을 함께 저장합니다.</summary>
    public static class CloudConceptCapture
    {
        public const int CaptureWidth = 1280;
        public const int CaptureHeight = 720;
        public const float CaptureFieldOfView = 68f;

        private const string OutputDirectory = "ConceptV2";
        private const string BaselineDirectory = "Screenshots/StyleMotion-Final";

        /// <summary>촬영 시점이 어디에서 왔는지와 원점 보정 전후 좌표를 기록합니다.</summary>
        [Serializable]
        public sealed class PoseDescription
        {
            public int pose;
            public string name;
            public string basis;

            public string sourceManifest;
            public string sourceManifestSha256;
            public int sourceFrame = -1;

            public Vector3 densityPosition;
            public Vector3 samplingOffset;
            public Vector3 worldPosition;
            public Quaternion worldRotation;
            public Vector3 eulerAngles;
        }

        /// <summary>기존 촬영 기록의 경로 정보만 읽으며 현재 생성 설정으로 시점을 다시 계산하지 않습니다.</summary>
        [Serializable]
        private sealed class BaselineManifest
        {
            public CloudStyleCapture.PathDescription path;
        }

        /// <summary>프로필 이름뿐 아니라 실제 저장값도 남겨 A/B의 입력 차이를 확인할 수 있게 합니다.</summary>
        [Serializable]
        private sealed class ProfileSnapshot
        {
            public string type;
            public string name;
            public string assetPath;
            public string assetGuid;
            public string values;
        }

        /// <summary>활성 Volume의 원본 구성값을 기록합니다. 최종 혼합된 Volume stack의 측정값은 아닙니다.</summary>
        [Serializable]
        private sealed class VolumeSnapshot
        {
            public string name;
            public bool global;
            public float priority;
            public float weight;
            public float blendDistance;
            public string profileName;
            public string sharedProfilePath;
            public ProfileSnapshot[] components;
        }

        /// <summary>PNG와 함께 보관할 카메라, 조명, 프로필, 해상도 및 기준 시점의 입력 기록입니다.</summary>
        [Serializable]
        private sealed class CaptureManifest
        {
            public string label;
            public string capturedAtUtc;
            public string unityVersion;
            public string scenePath;
            public string imagePath;
            public bool wasPlaying;

            public PoseDescription camera;
            public int width = CaptureWidth;
            public int height = CaptureHeight;
            public float fieldOfView = CaptureFieldOfView;
            public float nearClipPlane;
            public float farClipPlane;
            public Matrix4x4 projectionMatrix;
            public string antiAliasing;
            public bool allowHdr;
            public int warmupRenders = 4;

            public float requestedHour;
            public float appliedHour;
            public float previousHour;
            public bool previousAutoAdvance;
            public float previousWindSpeed;
            public float captureWindSpeed;
            public float cloudResolutionScale;
            public int expectedCloudWidth;
            public int expectedCloudHeight;
            public CloudLightingState lighting;

            public Vector3 sceneLightDirection;
            public float viewToSceneLightAngle;
            public string cloudPassValues;
            public string clockValues;
            public string environmentSourceValues;
            public ProfileSnapshot[] profiles;
            public VolumeSnapshot[] volumeConfigurations;

            public string limitations = "Fixed baseline density-space poses, not camera-facing geometry. Pose names describe the 2026-09-21 baseline; changed shapes can move the surface away from an interior candidate. Volume entries are source configurations, not the final blended camera stack. PNG is a real Unity render, not a GPU timing measurement.";
        }

        /// <summary>
        /// label별 1280×720 PNG/JSON을 저장합니다. 예: CapturePose("A-baseline", 1, 12f),
        /// CapturePose("B-macro-light", 1, 12f). 같은 label/pose/hour 재호출은 해당 결과를 갱신합니다.
        /// pose 0은 홈, 1은 기존 Hero, 2는 측면, 3은 역광, 4는 내부 후보, 5는 운해 상공, 6은 층운입니다.
        /// </summary>
        public static string CapturePose(string label, int pose, float hour)
        {
            ValidateLabel(label);
            if (float.IsNaN(hour) || float.IsInfinity(hour) || hour < 0f || hour > 24f)
            {
                throw new ArgumentOutOfRangeException(nameof(hour), "촬영 시각은 0~24 범위의 유한한 값이어야 합니다.");
            }

            Camera camera = RequireCamera();
            LostSkiesCloudPass pass = RequirePass();
            CloudTimeOfDayController clock = RequireClock(pass);
            PoseDescription description = DescribePose(pose, pass);

            Vector3 savedPosition = camera.transform.position;
            Quaternion savedRotation = camera.transform.rotation;
            float savedFieldOfView = camera.fieldOfView;
            float savedAspect = camera.aspect;
            RenderTexture savedTarget = camera.targetTexture;
            RenderTexture savedActive = RenderTexture.active;
            float savedHour = clock.TimeOfDay;
            bool savedAutoAdvance = clock.autoAdvance;
            float savedWindSpeed = pass.windSpeed;

            string hourName = hour.ToString("0.##", CultureInfo.InvariantCulture);
            string relativeDirectory = Path.Combine(OutputDirectory, label);
            string filename = "pose-" + pose.ToString("00", CultureInfo.InvariantCulture) + "-hour-" + hourName;
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            Directory.CreateDirectory(Path.Combine(projectRoot, "Screenshots", relativeDirectory));

            try
            {
                // Camera.Render는 동기 호출이므로 날씨 Update를 진행하지 않고 바람·시간만 일시 고정합니다.
                pass.windSpeed = 0f;
                clock.autoAdvance = false;
                ApplyCaptureHour(clock, hour);
                if (Mathf.Abs(clock.TimeOfDay - hour) > 0.001f)
                {
                    throw new InvalidOperationException("현재 시간 프로필이 요청 시각을 지원하지 않습니다. requested="
                        + hourName + ", applied=" + clock.TimeOfDay.ToString("0.##", CultureInfo.InvariantCulture));
                }

                camera.transform.SetPositionAndRotation(description.worldPosition, description.worldRotation);
                camera.fieldOfView = CaptureFieldOfView;
                camera.aspect = (float)CaptureWidth / CaptureHeight;

                string imagePath = LostSkiesCloudTools.Capture(Path.Combine(relativeDirectory, filename), CaptureWidth, CaptureHeight);
                CaptureManifest manifest = BuildManifest(label, description, camera, pass, clock);
                manifest.imagePath = imagePath;
                manifest.requestedHour = hour;
                manifest.previousHour = savedHour;
                manifest.previousAutoAdvance = savedAutoAdvance;
                manifest.previousWindSpeed = savedWindSpeed;
                File.WriteAllText(Path.ChangeExtension(imagePath, ".json"), JsonUtility.ToJson(manifest, true));
                return imagePath;
            }
            finally
            {
                camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
                camera.fieldOfView = savedFieldOfView;
                camera.aspect = savedAspect;
                camera.targetTexture = savedTarget;
                RenderTexture.active = savedActive;
                pass.windSpeed = savedWindSpeed;

                try
                {
                    ApplyCaptureHour(clock, savedHour);
                }
                finally
                {
                    clock.autoAdvance = savedAutoAdvance;
                }
            }
        }

        /// <summary>경로 구분자를 허용하지 않아 label이 프로젝트 밖의 출력 경로로 해석되지 않게 합니다.</summary>
        private static void ValidateLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label) || label.Length > 64)
            {
                throw new ArgumentException("label은 1~64자의 문자, 숫자, - 또는 _로 지정해야 합니다.", nameof(label));
            }

            foreach (char character in label)
            {
                if (!char.IsLetterOrDigit(character) && character != '-' && character != '_')
                {
                    throw new ArgumentException("label에는 문자, 숫자, - 또는 _만 사용할 수 있습니다.", nameof(label));
                }
            }
        }

        /// <summary>기존 고정 경로의 좌표를 읽어 시드나 프리셋 변경 후에도 같은 공간을 촬영합니다.</summary>
        private static PoseDescription DescribePose(int pose, LostSkiesCloudPass pass)
        {
            PoseDescription description;
            switch (pose)
            {
                case 0:
                    description = new PoseDescription();
                    description.name = "Home";
                    description.basis = "LostSkiesCloudTools.OpenScene home: density-space (1222,3300,-6800), Euler (10,15,0).";
                    description.densityPosition = new Vector3(1222f, 3300f, -6800f);
                    description.worldRotation = Quaternion.Euler(10f, 15f, 0f);
                    break;
                case 1:
                    description = ReadBaselinePose("orbit-12", 6);
                    description.name = "Original Hero";
                    description.basis = "Final 120-frame orbit frame 6 used by Style-Hero images; not the older 24-frame StyleMotion path.";
                    break;
                case 2:
                    description = ReadBaselinePose("orbit-12", 26);
                    description.name = "Side light baseline";
                    description.basis = "Final orbit frame 26: horizontal view approximately 90 degrees from the baseline Day sun azimuth 55. Camera remains fixed when time changes.";
                    break;
                case 3:
                    description = ReadBaselinePose("orbit-12", 115);
                    description.name = "Back light baseline";
                    description.basis = "Final orbit frame 115: horizontal view approximately aligned with baseline sun azimuth 55-60. Camera remains fixed when time changes.";
                    break;
                case 4:
                    description = ReadBaselinePose("traverse-12", 60);
                    description.name = "Inside baseline cloud";
                    description.basis = "Final traverse frame 60 near the baseline cloud center. Validate density again if shape or placement changes.";
                    break;
                case 5:
                    description = new PoseDescription();
                    description.name = "Above cloud sea";
                    description.basis = "Home XZ and the existing 9000m above-cloud flight altitude; fixed downward pitch 35, yaw 15.";
                    description.densityPosition = new Vector3(1222f, 9000f, -6800f);
                    description.worldRotation = Quaternion.Euler(35f, 15f, 0f);
                    break;
                case 6:
                    description = ReadBaselinePose("ribbon-12", 0);
                    description.name = "Ribbon upper face";
                    description.basis = "Final ribbon frame 0: above the actual tilted ribbon plane; fixed center selected by baseline seed 73 and salts 140-145.";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pose), "pose는 0~6 범위여야 합니다.");
            }

            description.pose = pose;
            description.samplingOffset = Vector3.zero;
            if (pass.worldOrigin != null)
            {
                description.samplingOffset = pass.worldOrigin.SamplingOffset;
            }

            description.worldPosition = description.densityPosition - description.samplingOffset;
            description.eulerAngles = description.worldRotation.eulerAngles;
            return description;
        }

        /// <summary>검증된 기존 경로의 한 프레임과 원본 해시를 읽으며 없거나 잘못된 기록은 명시적으로 거부합니다.</summary>
        private static PoseDescription ReadBaselinePose(string pathName, int frame)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string relativePath = Path.Combine(BaselineDirectory, pathName, "poses.json");
            string absolutePath = Path.Combine(projectRoot, relativePath);
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException("고정 촬영 시점의 기준 manifest가 필요합니다.", absolutePath);
            }

            byte[] bytes = File.ReadAllBytes(absolutePath);
            BaselineManifest manifest = JsonUtility.FromJson<BaselineManifest>(File.ReadAllText(absolutePath));
            if (manifest == null || manifest.path == null || manifest.path.poses == null
                || manifest.path.totalFrames != CloudStyleCapture.TotalFrames || frame >= manifest.path.poses.Length)
            {
                throw new InvalidDataException("120프레임 기준 촬영 경로를 읽을 수 없습니다: " + absolutePath);
            }

            CloudStyleCapture.CameraPose source = manifest.path.poses[frame];
            if (source == null || source.frame != frame)
            {
                throw new InvalidDataException("기준 경로의 프레임 번호가 일치하지 않습니다: " + absolutePath);
            }

            PoseDescription description = new PoseDescription();
            description.sourceManifest = relativePath;
            description.sourceFrame = frame;
            description.densityPosition = source.densityPosition;
            description.worldRotation = source.worldRotation;
            using (SHA256 hash = SHA256.Create())
            {
                description.sourceManifestSha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }

            return description;
        }

        /// <summary>시간 전환의 비직렬화 내부 상태를 건드리지 않고 시각만 임시 적용하거나 복원합니다.</summary>
        private static void ApplyCaptureHour(CloudTimeOfDayController clock, float hour)
        {
            // SetTime은 진행 중 전환을 취소하므로 편집기 전용 serialized 접근으로 촬영 전 상태를 보존합니다.
            SerializedObject serializedClock = new SerializedObject(clock);
            SerializedProperty property = serializedClock.FindProperty("timeOfDay");
            if (property == null || property.propertyType != SerializedPropertyType.Float)
            {
                throw new InvalidOperationException("CloudTimeOfDayController의 시각 저장 계약을 찾지 못했습니다.");
            }

            property.floatValue = hour;
            serializedClock.ApplyModifiedPropertiesWithoutUndo();
            clock.ApplyCurrent();
        }

        /// <summary>실제 촬영 시점의 카메라와 구름 입력을 수집하며 품질·후처리 설정은 임의로 바꾸지 않습니다.</summary>
        private static CaptureManifest BuildManifest(string label, PoseDescription description, Camera camera,
            LostSkiesCloudPass pass, CloudTimeOfDayController clock)
        {
            CaptureManifest manifest = new CaptureManifest();
            manifest.label = label;
            manifest.capturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            manifest.unityVersion = Application.unityVersion;
            manifest.scenePath = SceneManager.GetActiveScene().path;
            manifest.wasPlaying = Application.isPlaying;
            manifest.camera = description;
            manifest.nearClipPlane = camera.nearClipPlane;
            manifest.farClipPlane = camera.farClipPlane;
            manifest.projectionMatrix = camera.projectionMatrix;
            manifest.allowHdr = camera.allowHDR;
            HDAdditionalCameraData cameraData = camera.GetComponent<HDAdditionalCameraData>();
            if (cameraData != null)
            {
                manifest.antiAliasing = cameraData.antialiasing.ToString();
            }

            manifest.appliedHour = clock.TimeOfDay;
            manifest.captureWindSpeed = pass.windSpeed;
            manifest.cloudResolutionScale = pass.resolutionScale;
            manifest.expectedCloudWidth = Mathf.CeilToInt(CaptureWidth * pass.resolutionScale / 8f) * 8;
            manifest.expectedCloudHeight = Mathf.CeilToInt(CaptureHeight * pass.resolutionScale / 8f) * 8;
            manifest.lighting = clock.CurrentLighting;
            if (clock.sun != null)
            {
                manifest.sceneLightDirection = -clock.sun.transform.forward;
                manifest.viewToSceneLightAngle = Vector3.Angle(camera.transform.forward, manifest.sceneLightDirection);
            }

            manifest.cloudPassValues = JsonUtility.ToJson(pass, true);
            manifest.clockValues = EditorJsonUtility.ToJson(clock, true);
            if (clock.weatherSource != null)
            {
                manifest.environmentSourceValues = EditorJsonUtility.ToJson(clock.weatherSource, true);
            }

            manifest.profiles = new ProfileSnapshot[]
            {
                SnapshotProfile(pass.styleProfile),
                SnapshotProfile(pass.skyProfile),
                SnapshotProfile(pass.oceanProfile),
                SnapshotProfile(clock.profile)
            };
            manifest.volumeConfigurations = SnapshotVolumes();
            return manifest;
        }

        /// <summary>ScriptableObject의 에셋 식별자와 현재 메모리 값을 함께 기록합니다.</summary>
        private static ProfileSnapshot SnapshotProfile(UnityEngine.Object profile)
        {
            if (profile == null)
            {
                return null;
            }

            ProfileSnapshot snapshot = new ProfileSnapshot();
            snapshot.type = profile.GetType().FullName;
            snapshot.name = profile.name;
            snapshot.assetPath = AssetDatabase.GetAssetPath(profile);
            snapshot.assetGuid = AssetDatabase.AssetPathToGUID(snapshot.assetPath);
            snapshot.values = EditorJsonUtility.ToJson(profile, true);
            return snapshot;
        }

        /// <summary>Volume 복제본을 새로 만들지 않고 현재 활성 구성과 컴포넌트별 값을 기록합니다.</summary>
        private static VolumeSnapshot[] SnapshotVolumes()
        {
            List<VolumeSnapshot> snapshots = new List<VolumeSnapshot>();
            Volume[] volumes = UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
            foreach (Volume volume in volumes)
            {
                if (!volume.isActiveAndEnabled)
                {
                    continue;
                }

                VolumeProfile profile = volume.sharedProfile;
                if (volume.HasInstantiatedProfile())
                {
                    profile = volume.profile;
                }

                if (profile == null)
                {
                    continue;
                }

                VolumeSnapshot snapshot = new VolumeSnapshot();
                snapshot.name = volume.name;
                snapshot.global = volume.isGlobal;
                snapshot.priority = volume.priority;
                snapshot.weight = volume.weight;
                snapshot.blendDistance = volume.blendDistance;
                snapshot.profileName = profile.name;
                snapshot.sharedProfilePath = AssetDatabase.GetAssetPath(volume.sharedProfile);
                List<ProfileSnapshot> components = new List<ProfileSnapshot>();
                foreach (VolumeComponent component in profile.components)
                {
                    components.Add(SnapshotProfile(component));
                }

                snapshot.components = components.ToArray();
                snapshots.Add(snapshot);
            }

            return snapshots.ToArray();
        }

        /// <summary>실제 MainCamera를 촬영 대상으로 가져옵니다.</summary>
        private static Camera RequireCamera()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                throw new InvalidOperationException("MainCamera 태그가 지정된 게임 카메라가 필요합니다.");
            }

            return camera;
        }

        /// <summary>활성 CustomPassVolume에 연결된 기존 구름 렌더 패스를 찾습니다.</summary>
        private static LostSkiesCloudPass RequirePass()
        {
            CustomPassVolume[] volumes = UnityEngine.Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None);
            foreach (CustomPassVolume volume in volumes)
            {
                if (!volume.isActiveAndEnabled)
                {
                    continue;
                }

                foreach (CustomPass customPass in volume.customPasses)
                {
                    LostSkiesCloudPass pass = customPass as LostSkiesCloudPass;
                    if (pass != null && pass.enabled)
                    {
                        return pass;
                    }
                }
            }

            throw new InvalidOperationException("활성 LostSkiesCloudPass가 필요합니다.");
        }

        /// <summary>실제 구름 패스에서 사용하는 공유 시간 공급자만 조정합니다.</summary>
        private static CloudTimeOfDayController RequireClock(LostSkiesCloudPass pass)
        {
            CloudTimeOfDayController clock = pass.environmentSource as CloudTimeOfDayController;
            if (clock == null || !clock.isActiveAndEnabled || clock.profile == null)
            {
                throw new InvalidOperationException("시간 프로필을 가진 활성 CloudTimeOfDayController가 필요합니다.");
            }

            return clock;
        }
    }
}
