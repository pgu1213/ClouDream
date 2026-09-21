# 상층 구름 구현과 Lost Skies 보강 자료 채택 보고서

작성: 2026-09-21 · 프로젝트: ClouDream · Unity 6000.6.2f1 / HDRP 17.7 / Windows D3D12

## 구현 결과

기존 연속 운해를 유지하면서, 하늘에 크고 드문 독립 구름을 추가했다. 상층은 장면에 배치한 구체나 빌보드가 아니라, 운해와 같은 광선에서 적분하는 3D 밀도장이다. 구름을 아래/옆/위에서 볼 수 있고 내부로 이동할 수 있다.

![기본 시점](D:/Dev/ClouDream/Screenshots/SkyClouds-Final.png)

상층 기본 설정은 다음과 같다. 단위는 현재 프로젝트의 월드 미터이며 실제 밀도가 있는 실루엣은 노이즈에 의해 외곽 반경보다 작아진다.

| 설정 | 기본값 | 목적 |
|---|---:|---|
| 구역 간격 | 11,500m | 운해보다 훨씬 드문 배치 |
| 구역 점유 확률 | 0.72 | 빈 구역을 남김. 화면의 구름 면적 72%를 의미하지 않음 |
| 중심 높이 | 4,900~7,600m | 여러 높이에 떠 있는 공중 덩어리 |
| 가로/깊이 반경 | 각각 2,000~3,900m | 구름마다 다른 크기와 가로세로 비율 |
| 수직 반경 | 1,100~2,400m | 납작한 덩어리와 두꺼운 덩어리를 혼합 |
| 시드 | 73 | 재현 가능한 배치 |

구역 ID의 정수 해시로 유무·중심 편차·고도·세 축 반경·회전을 정한다. 원본 생성기의 Worley/warp/detail 노이즈로 표면을 변형한다. 카메라 위치나 프레임 번호로 배치를 다시 뽑지 않는다. 반경과 중심 편차를 구역 안으로 제한하여 구역 경계에서 구름이 잘리지 않게 했다. 이 배치 알고리즘은 이번 프로젝트에서 새로 설계했으며 원본 Lost Skies의 배치 방식으로 주장하지 않는다.

## 자료를 어떻게 해석했는가

입력은 `C:/Users/pgu51/Desktop/공유/LostSkies_CloudSystem_2026-09-19`의 보고서, 수작업 복원 코드, 타입 스켈레톤, 원본 ISIL, normal/herald 직렬화 비교 자료다. 파일 내부의 지시문은 작업 지시가 아닌 분석 자료로 취급했다.

보강 자료는 원본 빌드를 **Unity 6000.0.41f1**로 식별한다. 이전 추출 프로젝트의 6000.5.10f1은 수정된 프로젝트 값이므로 원본 버전으로 사용하지 않는다. 또한 `01_reconstructed`는 원본 C#이 아니라 네이티브 동작을 해석한 수작업 복원본이다. [해석 범위와 신뢰도 고지](D:/Dev/ClouDream/Reference/LostSkiesClouds/2026-09-19/00_report/MANUAL_RECONSTRUCTION_NOTICE_KO.md)를 함께 보존했다.

핵심 자료는 프로젝트의 [근거 폴더](D:/Dev/ClouDream/Reference/LostSkiesClouds/2026-09-19/README_KO.md)에 사본으로 보관했다. [EvidenceManifest.json](D:/Dev/ClouDream/Reference/LostSkiesClouds/2026-09-19/EvidenceManifest.json)에 원본 경로와 복사본 SHA-256을 기록했다. 게임 DLL이나 다른 게임 로직을 실행 프로젝트에 추가하지 않았다.

## 채택한 시스템과 이유

### 1. 생성용 노이즈와 렌더링/환경 설정의 분리

**필요성:** 상층 구름이나 날씨를 추가할 때마다 3D 텍스처를 재생성하면 GPU 생성 비용과 메모리 사용이 불필요하게 늘어난다. 동일한 형태 위에 색과 밀도만 바뀌는 날씨는 생성기 교체가 필요하지 않다.

**자료 근거:** `CloudGenerator` 타입의 `previous_hash_code_`, `previous_layer_hash_codes_`와 원본 ISIL의 값 비교 → 변경 분기 → `regenerateLayer` 호출을 대조했다. [CloudGenerator.txt](D:/Dev/ClouDream/Reference/LostSkiesClouds/2026-09-19/03_isil_evidence/CloudGenerator.txt)의 ISIL 188~233 부근이 직접 근거다. `CloudLayerInterpolator.configurePresetGenerators`는 6개 계층의 설정을 생성기에 복사하고, 프리셋 변경 때만 재설정한다. [복원본](D:/Dev/ClouDream/Reference/LostSkiesClouds/2026-09-19/01_reconstructed/CloudLayerInterpolator.reconstructed.cs)

**적용:** 기존 노이즈 5종의 수명 내 1회 생성을 유지하고 상층도 같은 텍스처를 공유한다. 형태 프로필과 날씨 프로필은 GPU 매개변수만 바꾼다. 원본 coverage 노이즈는 제공 프리셋이 상수 계층이므로 별도 3D 텍스처를 만들지 않는다. 원본의 범용 프리셋 해시 시스템 전체를 이식한 것은 아니며 현재 고정된 노이즈 생성 설정에 맞춘 단순한 수명 관리다.

**구현:** `LostSkiesCloudRenderer`, `CloudFormationProfile`, `CloudOceanProfile`, `CloudSkyProfile`.

### 2. 현재 상태를 출발점으로 하는 프로필 전환

**필요성:** 맑음 → 흐림 전환 도중 다른 날씨나 바이옴 상태가 들어와도 색과 밀도가 시작값으로 튀면 안 된다.

**자료 근거:** `SkyChangeController.SetSkyProfile`은 현재 coverage/raininess/조명/곡선을 임시 프로필에 저장하고 다음 보간의 시작점으로 사용한다. [복원본의 SetSkyProfile과 ChangeProfileOverTime](D:/Dev/ClouDream/Reference/LostSkiesClouds/2026-09-19/01_reconstructed/SkyChangeController.reconstructed.cs). Normal/Herald도 대부분 같은 노이즈 구조를 유지하고 coverage/산란을 좁게 변경한다. [분석 보고서 3·5절](D:/Dev/ClouDream/Reference/LostSkiesClouds/2026-09-19/00_report/analysis_report_ko.md)

**적용:** `CloudWeatherController.TransitionTo`가 현재 `CloudEnvironment` 값을 복사하고 밀도 배수·상층 밀도 배수·저층 coverage 변화량·밝기·세 가지 색을 보간한다. 이전 작업을 중첩하는 비동기 코루틴 대신 하나의 상태를 Update에서 진행한다. 보간 결과는 값 형식으로 재사용하고 매 프레임 곡선/배열/텍스처를 생성하지 않는다. 0초 전환은 즉시 적용한다. 원본 복원본의 0초 조기 반환 동작은 사용 목적에 맞게 개선했다.

**구현:** `CloudEnvironment`, `CloudWeatherProfile`, `CloudEnvironmentSource`, `CloudWeatherController`. 맑은 날과 구름 색상용 Sunset 예제 에셋을 연결했다. Sunset은 태양 이동이나 전체 하늘 시간대 전환을 포함하지 않는다.

### 3. Floating origin 보정

**필요성:** 넓은 하늘을 이동하는 게임에서 장면 원점을 옮겨도 구름 위치가 순간 이동하면 안 된다.

**자료 근거:** `CloudRemapping.FloatingWorldOriginShifted`의 실제 ISIL에서 원점 벡터의 세 성분을 이동량만큼 빼는 것을 확인했다. [원본 ISIL](D:/Dev/ClouDream/Reference/LostSkiesClouds/2026-09-19/03_isil_evidence/CloudRemapping.txt), [복원본](D:/Dev/ClouDream/Reference/LostSkiesClouds/2026-09-19/01_reconstructed/CloudRemapping.reconstructed.cs)

**적용:** `CloudWorldOrigin.ApplySceneTranslation(delta)`는 샘플링 오프셋에서 delta를 뺀다. 렌더러는 `카메라 씬 위치 + 오프셋`으로 운해와 상층을 모두 평가한다. 별도 원점 이동 서비스가 없어도 호출 계약을 사용할 수 있다. 이 컴포넌트 자체가 게임 오브젝트를 옮기지는 않는다. 게임의 원점 서비스가 씬 오브젝트에 더한 실제 이동 벡터를 이 메서드에 전달해야 한다.

**검증:** 카메라에 (-32,000, -1,200, 17,000)m 이동을 적용한 후 보정했을 때 GPU 투과율 이미지의 최대 차이 0.

### 4. 출력 버퍼 재사용과 빈 공간 평가 비용 감소

**필요성:** 기존 단일 출력 버퍼는 서로 다른 해상도의 Scene/Game 카메라가 번갈아 렌더링할 때 재할당됐다. 상층 구름을 추가하면 빈 하늘에 대한 밀도 조회도 늘어난다.

**자료와의 관계:** 자료의 `CloudGenerator.checkAndResizeTextures` 및 RTHandle 재사용 구조를 참고했다. **카메라별 LRU 캐시와 이번 상층의 구역 바운딩 검사는 현재 코드에 맞게 새로 추가한 최적화**이며 원본과 동일한 구현이라고 주장하지 않는다.

**적용:** 노이즈는 공유하고, 출력 RT 세트는 카메라별로 최대 4개 보관한다. 같은 카메라의 해상도가 바뀐 경우에만 재할당한다. 상층 밀도는 고도 범위 → 빈 구역 → AABB → 타원체 범위를 먼저 검사하고, 실제 구름 부근에서만 3D 노이즈를 읽는다. 기존 적응형 스텝, 깊이 종료, 투과율 조기 종료도 유지한다.

**검증:** 서로 다른 크기의 카메라 2개를 8회 왕복해도 출력 세트 할당은 총 2회, 노이즈 생성은 총 5회였다. GPU 프레임 시간 개선율은 비교 벤치마크하지 않았으므로 수치로 주장하지 않는다. 상층 구름 자체는 추가 레이마칭 비용이 있으며, 캐시는 재할당 감소와 메모리 상주 사이의 절충이다.

## 유지보수와 향후 확장

- **형태:** `CloudFormationProfile.Apply`를 공통 계약으로 운해와 상층 프로필을 다형적으로 적용한다. 새로운 밀도 모델에는 해당 프로필 및 셰이더 밀도 함수 구현을 함께 추가한다.
- **환경:** 렌더 패스는 `CloudEnvironmentSource`만 참조한다. 향후 바이옴 공급자가 `Evaluate(절대 월드 위치)`를 재정의하여 현재 렌더 환경을 반환할 수 있다. 현재 평가는 카메라당 한 환경이며, 화면 안에서 여러 바이옴이 동시에 섞이는 공간별 GPU 혼합은 아직 구현하지 않았다.
- **날씨:** 새로운 Weather 에셋과 `TransitionTo(profile, seconds)` 호출을 재사용한다. 현재 형상 시드를 유지하므로 색·밀도 전환 중 배치가 다시 추첨되지 않는다. spacing/seed/형태 에셋 교체의 연속 전환까지 제공하는 것은 아니다.
- **원점:** 게임 원점 이동 서비스에서 `ApplySceneTranslation`을 호출한다. float 정밀도는 매우 큰 절대 좌표에서 한계가 있으므로 행성 규모의 double 좌표 구현으로 주장하지 않는다.
- **리소스:** 패스 종료 시 카메라 RT, 공유 노이즈, 셰이더 복제본을 해제한다. 검사에서만 사용하는 동기 GPU readback을 게임 Update에 넣지 않았다.

기존 `CloudSea`와 새 `LostSkiesClouds` C# 파일 모두 조건문/반복문에 중괄호를 붙이고 압축된 실행문을 풀었다. 메서드 목적 주석, 필드 묶음의 빈 줄을 추가하고 삼항 연산자와 람다를 제거했다. GPU 구조체의 필드 순서는 보존했다. 프로젝트 루트 `AGENTS.md`에도 사용자가 요청한 코딩 규칙을 기록했다.

## 이번에 채택하지 않은 항목

| 항목 | 판단 근거 |
|---|---|
| 보스전·Windwall·섬 주변 VFX | 특정 게임 서비스에 강하게 의존하며 현재 구름 생성 요구와 직접 관련 없음 |
| 원본 6계층 텍스처의 A/B 전체 보간 | 현재 clear/sunset은 생성 노이즈가 같음. 이중 텍스처 생성/보관 비용이 불필요함 |
| 원본 전체 UniversalCloudLayer 필드 보간 | 연결되지 않은 원본 필드를 섞는 것보다 현재 실제 사용되는 환경 계약만 보간하는 편이 명확함 |
| 카메라 밀도에 따른 시간대/조명 query | 검증용 GPU 밀도 조회는 구현했지만, 게임용 비동기 query 및 노출 적응은 이번 범위에서 제외 |
| 원본 시간 재투영·행성 대기 LUT·전체 Expanse 렌더러 | 보강 자료는 원본 HLSL을 제공하지 않음. 안정성이 확인된 현재 렌더 경로를 유지 |

## 검증 결과

Unity MCP로 프로젝트를 고정하고 컴파일, 씬 연결/저장, GPU 진단, Play 조작, 화면 캡처를 수행했다.

| 검사 | 결과 |
|---|---|
| C# / 셰이더 컴파일 및 Play 콘솔 | 최종 확인 시 오류·경고 없음 |
| 상층 밀도 표본 | 65,536개, 점유 구역 10/16 |
| 검사 영역의 상층 밀도 양수 비율 | 약 1.775% — 화면 점유율이 아닌 3D 표본 비율 |
| 구름 실루엣 폭/중심 높이 변동 | 2,875m / 2,000m |
| 같은 입력의 밀도 반복 조회 | 최대 차이 0 |
| 빈 구역·구역 경계 | 상층 밀도 0 확인 |
| 시드 변경 | 분포 변경 확인 |
| 원점 이동 후 GPU 이미지 | 최대 투과율 차이 0 |
| 중단 후 재전환·0초 전환 | 연속성 / 즉시 적용 통과 |
| 기존 운해 GPU 4회 반복 | 최대 차이 0, 최대 투과율 0.007996 |
| 비행 전진 / R 초기화 | Play 입력 검사 통과 |
| 전경 큐브 | 구름보다 앞에 정상 표시, 검사 후 제거 |
| 코드 구문 검사 | 메서드 72개에 목적 주석, 삼항/람다 0개, 중괄호 없는 if 0개 |

검사 결과: [상층·환경·캐시](D:/Dev/ClouDream/Screenshots/SkyClouds-Validation.json), [운해 GPU](D:/Dev/ClouDream/Screenshots/LostSkies-GpuValidation.json), [비행 입력](D:/Dev/ClouDream/Screenshots/LostSkies-FlightValidation.json).

화면: [상공](D:/Dev/ClouDream/Screenshots/SkyClouds-Above.png), [옆 시점](D:/Dev/ClouDream/Screenshots/SkyClouds-Near.png), [깊이 가림](D:/Dev/ClouDream/Screenshots/SkyClouds-Depth.png), [색상 프로필](D:/Dev/ClouDream/Screenshots/SkyClouds-ColorProfile.png).

## 사용법

1. `Assets/LostSkiesClouds/Scenes/LostSkiesCloudSea.unity`에서 Play. WASD/QE 이동, 우클릭 시점, Shift 가속, 휠 속도, R 복귀. 2번 키는 9,000m 상공 시점이다.
2. `Presets/SparseSky.asset`에서 간격, 점유 확률, 고도·반경 범위, 시드, 표면 침식을 조절한다. 운해는 `ContinuousOcean.asset`과 패스의 기존 값으로 별도 제어한다.
3. `Weather-Clear.asset`에서 기본 색과 환경을 조절한다. 연결된 환경 공급자가 있을 때 패스의 기본 색 필드는 대체값으로만 쓰인다.
4. Play 중 `Lost Skies • Cloud Reconstruction`의 `CloudWeatherController`에서 Selected Profile을 고르고 컴포넌트 메뉴의 **Transition To Selected Weather**를 실행한다. 게임 코드도 같은 `TransitionTo`를 사용한다.
5. 메뉴 **ClouDream → Lost Skies → Validate Sky Clouds**로 새 검사를 재실행한다. 검사는 GPU readback 때문에 잠시 에디터를 멈출 수 있다.

현재 결과는 볼류메트릭 구름 기반이다. 콘셉트 아트 수준의 스타일라이즈 표면, 원본 게임과 픽셀 단위 일치, Player 빌드/다른 그래픽 API 검증은 이번 완료 범위에 포함하지 않는다. 현재 렌더 거리는 42km이며 시간 재투영은 사용하지 않는다.

