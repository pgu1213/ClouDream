from pathlib import Path
root=Path('Assets/LostSkiesClouds/Runtime')
p=root/'CloudWeatherController.cs'
s=p.read_text(encoding='utf-8-sig')
s=s.replace('public float transitionSeconds = 10f;\n        private', 'public float transitionSeconds = 10f;\n\n        // 전환 도중에도 현재 값을 보존하는 환경 상태입니다.\n        private')
s=s.replace('private CloudEnvironment transitionStart;\n\n        private CloudEnvironment transitionTarget;\n        private float duration;', 'private CloudEnvironment transitionStart;\n        private CloudEnvironment transitionTarget;\n\n        private float duration;')
p.write_text(s,encoding='utf-8')
p=root/'LostSkiesCloudPass.cs'
s=p.read_text(encoding='utf-8-sig').replace('public Shader compositeShader;\n\n        public Light sun;', 'public Shader compositeShader;\n        public Light sun;')
s=s.replace('public Color highlightTint = new Color(1.12f, 1.08f, 0.97f);\n        private', 'public Color highlightTint = new Color(1.12f, 1.08f, 0.97f);\n\n        // 패스가 소유하고 종료 시 해제하는 렌더 상태입니다.\n        private')
p.write_text(s,encoding='utf-8')
p=root/'LostSkiesCloudRenderer.cs'
s=p.read_text(encoding='utf-8-sig')
s=s.replace('private const int MaximumCachedCameras = 4;\n        private', 'private const int MaximumCachedCameras = 4;\n\n        private')
s=s.replace('private int renderSequence;\n        public Universal', 'private int renderSequence;\n\n        public Universal')
s=s.replace('public CloudFormationProfile skyProfile;\n        public Vector3', 'public CloudFormationProfile skyProfile;\n\n        public Vector3')
s=s.replace('public float skyDensityMultiplier = 1f;\n        public RenderTexture', 'public float skyDensityMultiplier = 1f;\n\n        public RenderTexture')
p.write_text(s,encoding='utf-8')
# 블록 첫 줄의 불필요한 빈 줄만 정리하고 실제 필드 섹션 구분은 유지합니다.
for folder in [Path('Assets/LostSkiesClouds'),Path('Assets/CloudSea')]:
    for p in folder.rglob('*.cs'):
        s=p.read_text(encoding='utf-8-sig').replace('    {\n\n        [Header', '    {\n        [Header')
        p.write_text(s,encoding='utf-8')
