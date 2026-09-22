# Lost Skies 구름 분석 기반 볼류메트릭 재현

## 2026-09-22 컨셉 V2

현재 장면은 `Style-ConceptV2` / `Sky-ConceptV2` / `TimeOfDay-ConceptV2`를 사용한다. 기존 볼류메트릭 경로와 연속 운해를 유지하면서 큰 몸체, 렌더 실루엣, 조명용 형태를 분리했다. 낮의 청록 그림자, 석양의 따뜻한 면, 달빛 밤을 별도 팔레트로 조절한다.

- **ClouDream → Lost Skies → Concept V2 → Apply**: V2 연결 및 장면 저장. 이미 있는 프리셋의 조정값은 보존한다.
- **Restore September 21 Look**: 이전 Layered 룩 연결 및 장면 저장. 기준 낮 캡처와 픽셀 차이 0으로 복귀를 확인했다.
- Play에서 **4 낮 / 5 석양 / 6 황혼 / 7 달빛 밤**, **T 시간 재생·정지**. 현재 데모는 12시부터 22시까지 진행 후 멈추며 24시간 순환은 아니다.
- 현재 구름 셰이더는 **Windows Direct3D12 전용**이다. DX11은 지원하지 않는다. 다른 API에서는 원인을 알리는 메시지와 함께 구름 패스를 중단한다.

[채택 근거·실험·검증·한계](D:/Dev/ClouDream/Docs/CloudConceptV2-2026-09-22.md) · [720개 시점 검토](D:/Dev/ClouDream/Screenshots/ConceptMotion-Final/review.html) · [네 시간대 비교](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/time-of-day.jpg) · [작업 전 전체 백업 복구 안내](D:/Dev/ClouDream/Backups/Before-ConceptV2-2026-09-22/RESTORE.md).

현재는 컨셉을 향한 구현 단계다. 둥근 로브 반복, 매끈한 띠 하부, 내부 비행의 약한 방향감이 남아 있다. 이동 자료는 오프라인 카메라 렌더이며 실시간 FPS를 뜻하지 않는다. 아래 날짜별 섹션은 당시 구현 기록이고 현재 동작은 이 섹션과 V2 보고서를 기준으로 한다.

## 2026-09-21 입체 표면 스타일

기존 운해·상층 배치·시간대·날씨·원점 이동 구조에 `CloudStyleProfile`을 추가했다. 둥근 로브의 거리장을 실제 볼륨 밀도로 바꾸고, 두 크기의 굴곡과 밀도 법선 조명으로 넓은 면을 표현한다. Layered 방식의 얇은 띠구름도 두께가 있는 3D 밀도다.

비교용 SoftNoise / SculptedLobes / LayeredBillows 세 방식을 보존했으며 현재 컨셉 개발 기준은 LayeredBillows다. [구현 기록과 선택 근거](D:/Dev/ClouDream/Docs/CloudStyle-2026-09-21.md)에 초기 비교·비용·검증 결과와 한계를 정리했다.

- **Apply Concept Cloud Style**: `Style-Layered` / `Sky-Concept` / `TimeOfDay-Concept`를 연결하고 관련 장면 저장.
- **Use Previous Volumetric Look**: 스타일 해제 후 기존 `SparseSky` / `TimeOfDay-Lighting`으로 복귀하고 관련 장면 저장.
- 두 메뉴 모두 **ClouDream → Lost Skies** 아래에 있다. 완전한 소스 복구는 [작업 전 백업 절차](D:/Dev/ClouDream/Backups/Before-Stylization-2026-09-21/RESTORE.md)를 따른다.

[백업 ZIP](D:/Dev/ClouDream/Backups/Before-Stylization-2026-09-21/ClouDream-before-stylization.zip)과 [파일별 SHA-256](D:/Dev/ClouDream/Backups/Before-Stylization-2026-09-21/manifest.json)을 보존했다. Unity 재생성 캐시를 제외한 프로젝트 복구용 백업이다.

Sculpted/Layered에 60m 간격 조명 보간을 적용하고 D3D12 셰이더를 DXC로 컴파일했다. 최종 중앙값은 기존 분기 9.83ms / 스타일 61.48ms다. 초기 스타일 93.40ms보다 약 34.2% 줄었지만 컴파일러도 바뀌었으므로 조명 캐시 단독 효과는 아니다. 여전히 기존 분기보다 약 6.25배 고비용이며, 이 수치는 **Editor 동기 카메라 렌더+GPU 읽기 완료 시간**으로 게임 FPS가 아니다. [측정 조건·결과](D:/Dev/ClouDream/Screenshots/Style-Performance.json)

[스타일 GPU 검사](D:/Dev/ClouDream/Screenshots/Style-Validation.json), [조명 검사](D:/Dev/ClouDream/Screenshots/CloudLighting-Validation.json), [장면 노출·가림](D:/Dev/ClouDream/Screenshots/CloudLighting-SceneValidation.json), [Play 입력](D:/Dev/ClouDream/Screenshots/LostSkies-FlightValidation.json)이 통과했다. 이전 룩 복귀도 [캡처](D:/Dev/ClouDream/Screenshots/Style-Restored-Previous.png) 후 컨셉 스타일을 재적용·저장했다.

orbit/traverse/sea/ribbon을 낮·일몰 각각 120프레임, 총 960장으로 촬영했다. [검토 페이지](D:/Dev/ClouDream/Screenshots/StyleMotion-Final/review.html)에서 원본 PNG를 확인한다. 검토는 오프라인 캡처·접촉 시트·선택 인접 프레임 비교이며 모든 프레임의 실시간 재생 QA를 의미하지 않는다. 순광 방향의 약한 대비와 태양 진입 시 큰 광륜은 남은 한계다.

최종 대표 시점: [낮](D:/Dev/ClouDream/Screenshots/Style-Hero-Day.png) · [일몰](D:/Dev/ClouDream/Screenshots/Style-Hero-Sunset.png) · [해질녘](D:/Dev/ClouDream/Screenshots/Style-Hero-Twilight.png).

## 2026-09-21 시간대 조명

기존 운해와 상층 형태를 유지하며 낮 **12:00**, 일몰 **18:00**, 해질녘 **19:15**의 태양·하늘·구름 조명을 연결했다. 설정은 [TimeOfDay-Lighting.asset](D:/Dev/ClouDream/Assets/LostSkiesClouds/Presets/TimeOfDay-Lighting.asset), 구현 근거와 운영 방법은 [시간대 조명 보고서](D:/Dev/ClouDream/Docs/CloudLighting-2026-09-21.md)에 기록했다.

- Play에서 **4 / 5 / 6**: 낮 / 일몰 / 해질녘까지 기본 6초 전환. 자동 재생은 멈춘다.
- **T**: 시간 재생/일시 정지. **H**: 안내 표시 전환.
- 기본 자동 재생은 180초에 낮부터 해질녘까지 진행하고 끝에서 멈춘다. 24시간 순환은 아직 제공하지 않는다.
- 편집 중 `CloudTimeOfDayController`의 `Preview/Day`, `Preview/Sunset`, `Preview/Twilight` 메뉴로 즉시 비교한다.

공유 `CloudLightingState`가 HDRP 태양·물리 기반 하늘·노출과 구름의 RGB 적분을 함께 제어한다. 배경 깊이에만 하늘 팔레트를 섞고, 주변광에 위쪽 밀도 조회 3회를 추가해 해질녘에도 굴곡을 표현한다. `CloudEnvironmentSource`와 `TintLighting()`을 통해 기존 날씨/바이옴 밀도와 색을 재사용한다. Volume은 소유 복제본으로 제어하고 연결 해제 시 원래 상태를 복구한다.

비교 이미지: [낮](D:/Dev/ClouDream/Screenshots/Lighting-Day.png) · [일몰](D:/Dev/ClouDream/Screenshots/Lighting-Sunset.png) · [해질녘](D:/Dev/ClouDream/Screenshots/Lighting-Twilight.png).
검증 출력: [GPU·전환·수명](D:/Dev/ClouDream/Screenshots/CloudLighting-Validation.json), [HDRP 노출·전경 가림](D:/Dev/ClouDream/Screenshots/CloudLighting-SceneValidation.json). 메뉴 **ClouDream → Lost Skies → Validate Time Of Day Lighting**에서 재검사한다.

새로 작성한 조명 구현이며 원본 소스 복사가 아니다. 주변광은 대기 LUT 직접 조회가 아닌 근사이고, 화면용 하늘 팔레트는 반사 프로브의 하늘과 다를 수 있다. 이후 추가한 표면 스타일의 진행 상태는 위 최신 섹션에 기록한다.

## 2026-09-21 상층 구름 확장

기존 운해 위에 크기·높이·비율·회전이 다른 큰 구름을 드문 간격으로 추가했다.
세부 채택 근거, 검증 결과, 확장 계약은 [구현 보고서](../../Docs/CloudSystem-Adoption-2026-09-21.md)에 기록했다.

- `Presets/SparseSky.asset`: 상층 구름의 간격, 점유 확률, 크기·고도 범위, 시드.
- `Presets/ContinuousOcean.asset`: 연속 운해의 하단과 두께.
- `Presets/Weather-Clear.asset`: 현재 기본 환경의 밀도 배수와 색상.
- `CloudWeatherController.TransitionTo(profile, seconds)`: 중간 재전환에도 이어지는 날씨/색상 보간.
- `CloudWorldOrigin.ApplySceneTranslation(delta)`: 게임 원점 이동 서비스가 씬에 더한 이동량을 전달하는 연결점.
- 메뉴 **ClouDream → Lost Skies → Validate Sky Clouds**: 상층 분포, 경계, 원점, 전환, 캐시 검사.

`CloudFormationProfile`은 형태 설정, `CloudEnvironmentSource`는 향후 날씨/바이옴 공급자의 공통 기반이다.
현재 바이옴 평가는 카메라당 한 환경이다. 여러 바이옴의 공간별 GPU 혼합은 아직 구현하지 않았다.

## 실행

`Assets/LostSkiesClouds/Scenes/LostSkiesCloudSea.unity`를 열고 Play.
메뉴: **ClouDream → Lost Skies → Create or Open Cloud Scene**.
빌드 목록 첫 번째 장면으로 등록되어 있다. 기존 `CloudSea` / `OutdoorsScene`은 유지한다.

WASD 이동, Q/E 높이, 우클릭 시점, Shift 가속, 휠 속도, R 초기화.
1 시작 시점, 2 상층 구름 위(9,000m), 3 구름 안, H 안내 숨기기.

`Lost Skies • Cloud Reconstruction`의 Custom Pass Volume에서 조절:

- **Tower Coverage / Cloud Density**: 기존 저층 타워의 분포와 전체 광학 밀도. 새 상층 형태는 SparseSky 에셋에서 조절한다.
- **Resolution Scale**: 기본 0.75. 1은 높은 선명도와 높은 GPU 비용. 0.5는 성능 우선.
- **Wind Speed**: Play 중 구름 이동.
- **Brightness / Shadow Tint / Highlight Tint**: 환경 공급자가 없을 때의 기본값. 현재 장면은 시간대 공급자가 Weather-Clear 환경을 감싼다. 시간대 조명에서 날씨 색은 ClearDay 대비 배수로 직접광/주변광에 반영된다.
- **Use Extracted Values**: 저장된 원본 매개변수 값을 사용하고 추가 운해와 상층 구름을 끈다. 원본 게임 렌더링과 동일한 모드는 아니다.
- **Render In Editor**: 편집 중 Game/Scene 뷰 표시.

## 실제로 가져온 자료

원본 위치: `C:/Users/pgu51/Desktop/공유/디컴파일/Lost-Skies_Decom/ExportedProject`.

- `Resources/CloudGenerator.asset`와 원본 meta: 원본 네이티브 GPU 노이즈 생성기. 파일을 그대로 가져왔다.
- `cloud_preset_ms10_normal_0.asset`, `cloud_preset_ms10_herald_0.asset`: 누락된 MonoBehaviour 스크립트를 설치하지 않도록 원문은 `Presets/Original-*.txt`, 숫자 설정은 JSON으로 보존했다. 기본 실행 장면은 normal을 사용한다.
- Expanse DLL에서 확인한 구름 설정 필드와 GPU 배치: `OriginalGpuLayouts.cs`, 프로젝트 루트 `Reference/LostSkiesClouds`의 분석 자료.

normal의 Base=Worley(4, 6 octaves, persistence .55), Structure=Perlin(12, 4, .75), Detail=Worley(12, 5, .75), Base Warp=Perlin(16, 4, .5), Detail Warp=Curl(8, 3, .3)를 원본 생성기로 생성한다. 밀도 곡선 16개 샘플도 원본 normal 값을 사용한다. 원본 High 품질의 해상도 표는 함수 본문이 없어 복구하지 못했으며, 현재 텍스처 해상도는 128³ / 64³ / 64³ / 64³ / 32³이다.

스타일 경로는 같은 생성기로 만든 128³ 단일 octave Worley를 최초 사용 때 하나 더 생성하고 캐시한다. 추가 입력을 사용하는 형태·조명 알고리즘은 이번 프로젝트에서 새로 작성했다.

다른 게임의 섬, 캐릭터, UI, VFX, 게임 DLL은 실행 프로젝트에 가져오지 않았다.

## 동일 복제와의 차이 — 중요

**이 장면은 원본 데이터를 사용한 재구현이며 Lost Skies의 완전한 동일 복제가 아니다.**

초기 추출된 `Expanse.dll`의 주요 함수는 IL2CPP 더미 본문이었다. 2026-09-19 보강 자료에는 네이티브 ISIL과 상태 전환 등의 수작업 복원본이 추가되었지만 원본 HLSL 소스는 제공되지 않는다. 원본 `CloudRenderer.asset`은 GPU 코드가 남아 있어 첫 출력까지 확인했지만, 현재 Unity에서 반복 실행 시 출력이 사라졌고 실험용 변형 재로딩/실행 중 `ComputeShader::BeforeDispatch` 네이티브 충돌도 발생했다. 원인은 완전히 규명되지 않았다. 해당 실험용 렌더러는 실행 Assets에서 제거했다. 현재 실행에는 문제가 확인된 렌더러 바이너리를 사용하지 않는다.

현재 `CloudRaymarch.compute`와 `CloudComposite.shader`는 새로 작성한 코드다. 월드 공간에서 실제 3D 밀도를 레이마칭하며, 빛 방향의 밀도 적분, 자기 그림자, 산란 근사, 불투명 물체의 깊이 가림을 처리한다. 하단의 연속 운해와 상층 구름을 하나의 밀도장으로 합쳐 구름 내부/하부에서도 동일한 적분을 사용한다.

원본 설정 중 density/coverage/structure/detail/warp/anisotropy 및 높이 밀도 샘플을 연결했다. 노이즈 배치 크기, 높이별 분포, 광학 계수의 스케일, 조명·색 합성, 하단 운해는 현재 장면에 맞게 재구현/조정했다. 저장된 JSON의 모든 필드를 원본과 동일한 의미로 적용한 것은 아니다. 원본의 대기 LUT, 커스텀 구름 프리미티브/스플라인, 전체 기상 연출, 시간 재투영은 복구하지 않았다. herald는 참고용 보존 자료이며 원본 normal/herald의 모든 필드를 혼합하는 전환은 제공하지 않는다. 새 CloudWeatherProfile은 현재 연결된 밀도·coverage 변화량·색상을 런타임에 전환한다.

목표 이미지는 형태·구도 방향의 참고다. 이번 결과는 볼류메트릭 기반이며, 컨셉 아트 수준의 스타일라이즈 표면 완성본은 아니다.

## 검증

아래는 스타일 추가 전 운해 경로의 Unity 6000.6.2f1 / HDRP 17.7 / Windows D3D12 MCP 검증 기록이다. 최신 스타일의 최종 검증 상태는 위 스타일 섹션과 별도 보고서를 확인한다.

- 원본 GPU 노이즈 5종 모두 공간 분포가 있으며 NaN 없음.
- 같은 입력으로 4회 GPU 렌더링: 투과율 최대 차이 0.
- 운해 아래쪽으로 향하는 96×96 광선: 최대 투과율 약 0.008, 하단 공백 없음.
- 전경 큐브가 구름보다 앞에 표시되는 깊이 가림 확인.
- Play 모드 이동 키 입력과 R 초기화 통과. 보고서는 루트 `Screenshots/LostSkies-FlightValidation.json`.
- 반복 Play 프레임 및 컴파일/콘솔 오류 확인.

검사 코드: `Editor/LostSkiesValidation.cs`. 이전 운해 캡처: `Screenshots/LostSkies-CloudSea.png`. 최신 상층 포함 캡처: `Screenshots/SkyClouds-Final.png`.

현재 제한: 낮은 내부 해상도와 레이마칭에 따른 부드러운 표면·샘플링 흔적, 시간 재투영 미구현, 원본 바이너리 노이즈 생성기의 플랫폼 의존성. Windows D3D12 외 그래픽 API와 Player 빌드는 별도로 검증하지 않았다.
