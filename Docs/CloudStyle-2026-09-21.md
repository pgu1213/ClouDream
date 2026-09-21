# 구름 표면 스타일라이즈 구현 기록

작성: 2026-09-21 · ClouDream · Unity 6000.6.2f1 / HDRP 17.7 / Windows D3D12

스타일 구현, 최종 계측, GPU·조명·Play 입력 검사와 960장의 연속 시점 캡처를 완료했다. 이동 품질은 오프라인 캡처·접촉 시트·선택한 인접 프레임을 검토한 범위로 보고한다. 모든 프레임의 실시간 재생 QA나 목표 FPS 달성을 의미하지 않는다.

기존의 연속 운해, 드문 상층 배치, 시간대 조명, 날씨/바이옴 공급자와 원점 이동 구조 위에 입체 표면 스타일을 추가했다. 큰 둥근 덩어리, 중간 크기의 굴곡, 얇고 긴 구름을 실제 3D 밀도로 구성한다. 카메라를 향하는 평면 그림이나 한 시점 전용 배경으로 대체하지 않는다. 기존 기능과 되돌아갈 경로를 유지하지만, 스타일 활성화 시 표면 밀도와 실루엣 자체는 달라진다.

![Unity 카메라에서 촬영한 최종 낮 구름](D:/Dev/ClouDream/Screenshots/Style-Hero-Day.png)

동일 시점 1600×900 비교: [낮](D:/Dev/ClouDream/Screenshots/Style-Hero-Day.png) · [일몰](D:/Dev/ClouDream/Screenshots/Style-Hero-Sunset.png) · [해질녘](D:/Dev/ClouDream/Screenshots/Style-Hero-Twilight.png). 시퀀스의 orbit 6번 위치를 실제 Unity 카메라로 촬영한 뒤 카메라를 원래 위치로 복구했다.

## 백업과 되돌리기

작업 전 상태는 [ClouDream-before-stylization.zip](D:/Dev/ClouDream/Backups/Before-Stylization-2026-09-21/ClouDream-before-stylization.zip), 파일별 SHA-256은 [manifest.json](D:/Dev/ClouDream/Backups/Before-Stylization-2026-09-21/manifest.json), 복구 절차는 [RESTORE.md](D:/Dev/ClouDream/Backups/Before-Stylization-2026-09-21/RESTORE.md)에 있다.

ZIP에는 `Assets`와 meta, `Packages`, `ProjectSettings`, `UserSettings`, 문서·참고 자료·캡처·보조 스크립트·프로젝트 규칙이 포함된다. Unity가 재생성하는 `Library/Temp/Logs`는 제외한 프로젝트 복구용 백업이다. 전체 복구는 새 빈 폴더에 풀고 Unity 6000.6.2f1로 여는 절차를 따른다. 현재 폴더에 덮어쓰기만 하면 이번 작업에서 추가한 파일이 남는다.

화면만 이전 방식으로 돌아가려면 **ClouDream → Lost Skies → Use Previous Volumetric Look**을 사용한다. 이 메뉴는 스타일을 해제하고 기존 `SparseSky`와 `TimeOfDay-Lighting`을 다시 연결한 뒤 관련 장면을 저장한다. 소스 전체를 되돌리는 백업 복구와는 범위가 다르다. **Apply Concept Cloud Style**은 컨셉 프로필을 연결하고 장면을 저장한다. [전환 도구](D:/Dev/ClouDream/Assets/LostSkiesClouds/Editor/CloudStyleSetup.cs)

이전 룩 복귀 메뉴를 실제 실행하여 [복귀 화면](D:/Dev/ClouDream/Screenshots/Style-Restored-Previous.png)을 촬영한 뒤 컨셉 스타일을 다시 적용하고 장면을 저장했다. ZIP을 새 폴더에 풀어 프로젝트 전체를 재실행하는 복구 시험까지 수행한 것은 아니다.

## 세 방식의 비교와 선택

| 방식 | 변경 내용 | 초기 비교에서 확인한 특징 | 현재 용도 |
|---|---|---|---|
| SoftNoise | 기존 밀도 형태에 높은 노이즈 mip과 약한 warp를 사용하고 미세 침식을 생략 | 잔잡음은 줄지만 연기처럼 흐린 표면과 작은 굴곡의 인상이 남는다 | 기존 룩과 가까운 대조군 |
| SculptedLobes | 둥근 로브를 부드럽게 연결한 거리장을 밀도로 변환 | 큰 면과 연속 운해가 읽히지만 초기 설정은 너무 매끈한 풍선 같은 덩어리로 보인다 | 큰 형태의 조절 기준 |
| LayeredBillows | 조형 로브에 중간·작은 굴곡, 일부 납작한 구름과 독립적인 3D 띠구름 추가 | 세로로 솟은 덩어리와 가로로 늘어난 구름이 함께 보여 컨셉의 공간 구성에 더 가깝다 | 현재 선택한 개발 기준 |

초기 비교 캡처: [SoftNoise](D:/Dev/ClouDream/Screenshots/Style-SoftNoise.png) · [Sculpted](D:/Dev/ClouDream/Screenshots/Style-Sculpted.png) · [Layered](D:/Dev/ClouDream/Screenshots/Style-Layered.png). 이후 파라미터와 조명이 계속 바뀌었으므로 이 세 이미지는 최종 버전의 정량 비교 자료가 아니다.

현재 선택은 LayeredBillows다. 컨셉에는 큰 세로 구름과 얇은 수평 구름이 함께 있고, 그 사이를 이동할 공간이 필요하기 때문이다. 다만 컨셉과 동일한 미술 품질을 달성했다는 뜻은 아니다. [최종 기본 시점 낮](D:/Dev/ClouDream/Screenshots/Style-Final-Day.png)과 [일몰](D:/Dev/ClouDream/Screenshots/Style-Final-Sunset.png)에서 표면과 실루엣을 비교할 수 있다.

## 구현과 기존 시스템의 연결

**프로필 확장.** [CloudStyleProfile](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudStyleProfile.cs)은 기존 `CloudFormationProfile`을 상속한다. 경계 부드러움, 큰/작은 굴곡의 주기와 강도, 넓은 명암 면, 주변광 차폐, 광학 밀도 배수를 같은 GPU 설정 경로로 전달한다. 별도의 구름 렌더러를 중복 실행하지 않는다. 스타일을 연결하지 않으면 기존 밀도 분기를 사용한다.

**조형 밀도장.** [CloudSculpting.hlsl](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudSculpting.hlsl)은 정규화 공간의 SDF 방식 거리장을 사용한다. 상층은 높이·중심·반경이 다른 로브를 smooth union으로 잇고, 셀 난수로 기울기와 무게중심을 바꾼다. 운해는 연속 바닥 위에 주변 셀의 낮은 타원체를 겹친다. 경계의 거리값을 유한 두께의 밀도로 바꾸므로 내부·측면·뒷면에서도 같은 구름을 만난다. 새 거리장은 원본 게임 코드의 복사본이 아니다.

**노이즈 계층.** 기존 다섯 노이즈를 유지하고, 원본 네이티브 생성기로 만든 128³ 단일 octave Worley 하나를 `_SculptNoise`로 추가한다. 다중 octave의 미세 침식 대신 두 크기의 둥근 굴곡을 얹기 위한 입력이다. 스타일 최초 렌더 시 생성하고 같은 렌더러 안에서 재사용한다. 스타일·시간·카메라 전환마다 다시 생성하지 않으며, 렌더러 종료 때 기존 노이즈와 함께 해제한다. [생성·캐시 코드](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/LostSkiesCloudRenderer.cs)

**입체 면 조명.** 구름 진입 경계를 이분 탐색으로 보정하고 밀도 기울기로 법선을 추정한다. 태양을 향하는 면은 밝게, 하늘을 향하는 면은 주변광 색을 받게 한다. 명암은 월드 밀도와 빛 방향에서 계산하며 화면 위에 고정된 색 띠를 그리지 않는다. 기존 시간대의 직접광·주변광·가장자리 산란과 `CloudEnvironment.TintLighting()`의 날씨 색 보정을 계속 사용한다. [적분과 조명](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute)

**얇은 구름.** LayeredBillows의 띠구름은 높이·회전·기울기가 다른 두께 있는 렌즈 형태의 3D 밀도다. 밑면이나 옆면에서 보는 장면도 같은 함수로 계산한다. 상층 점유율·밀도를 끄면 띠구름도 함께 사라지고, 상층 타워의 높이를 낮춰도 띠구름 고도가 광선 구간에서 잘리지 않도록 처리했다. 기존 날씨의 coverage는 깊은 운해 바닥을 유지하면서 둥근 표면의 부피에 반영한다.

**운영 프로필.** 현재 컨셉 연결은 [Style-Layered](D:/Dev/ClouDream/Assets/LostSkiesClouds/Presets/Style-Layered.asset), [Sky-Concept](D:/Dev/ClouDream/Assets/LostSkiesClouds/Presets/Sky-Concept.asset), [TimeOfDay-Concept](D:/Dev/ClouDream/Assets/LostSkiesClouds/Presets/TimeOfDay-Concept.asset)을 사용한다. 기존 상층/시간대 프로필을 남겨 비교와 복귀가 가능하다. 낮·일몰·해질녘의 4/5/6, 시간 재생 T, 안내 H와 비행 입력은 기존대로 사용한다.

최종 컨셉 일몰은 태양 고도 5°, 95,000Lux, 색 `(1, 0.60, 0.24)`, 직접광 배수 2.45를 사용한다. 주변광은 천정 `(0.51, 0.53, 0.74)`, 지평선 `(0.64, 0.46, 0.61)`, 강도 0.65이며 노출은 EV11.2다. 따뜻한 면과 보라 그늘을 구분하기 위한 미술 값이고 물리적 일몰 관측값의 재현은 아니다. 프로필 에셋이 조정의 기준이다.

## 최적화와 최종 비용

[조명 재사용 전 측정](D:/Dev/ClouDream/Screenshots/Style-Performance-Before-LightCache.json)은 같은 카메라·시간·상층 프로필에서 스타일 연결 여부를 비교했다. 출력은 1280×720, 실제 구름 타깃은 960×544, 방식별 준비 렌더 3회와 측정 8회다. RTX 4080 / D3D12 / Unity Editor에서 기존 분기 중앙값은 **21.4704ms**, 당시 Layered 분기는 **93.40255ms**였다. 당시 측정의 중앙값 비율은 약 4.35배다.

이 값은 `Camera.Render()`와 `AsyncGPUReadback.WaitForCompletion()`을 포함한 동기 완료 시간이다. CPU·GPU·읽기·동기화 대기가 함께 들어가므로 **게임 FPS나 구름만의 GPU 시간으로 환산하지 않는다.** 단일 시점이며 기존 분기를 먼저 측정했고, 에디터 부하와 드라이버 상태도 영향을 준다. [계측 구현](D:/Dev/ClouDream/Assets/LostSkiesClouds/Editor/CloudStylePerformance.cs)

Sculpted/Layered 경로는 60m 간격의 입사광 평가점 사이를 선형 보간한다. 밀도·투과율은 기존의 촘촘한 단계로 적분하면서 태양 방향 7회와 주변광 방향 3회의 밀도 조회 결과를 재사용한다. 새 표면 진입이나 빈 공간 통과 시 캐시를 무효화하여 앞 구름의 빛을 다음 구름에 이어 쓰지 않는다. 기존 무스타일/SoftNoise 경로는 매 단계 조명 평가를 유지한다.

중첩 밀도/조명 루프에서 발생한 FXC 컴파일 지연을 피하기 위해 이 컴퓨트 셰이더에 `#pragma use_dxc dx12`를 추가했다. Unity 공식 [UUM-77825](https://issuetracker.unity.com/issues/22492/compiler-timed-out-is-thrown-when-compiling-a-compute-shader)는 중첩 루프에서 FXC 처리가 매우 느려질 수 있으며 DXC 전환이 우회 방법이라고 설명한다. [Unity 6000.7 DXC 문서](https://docs.unity.com/en-us/engine/6000.7/manual/materials-and-shaders/shaders/shader-troubleshooting/shader-reducing/shader-dxc-compiler)는 `dx12`로 대상 API를 제한하는 문법을 명시한다. 문서 버전과 실제 실행 버전은 구분하며, 이 프로젝트에서는 **6000.6.2f1 / Windows D3D12**에서 컴파일과 실행을 확인했다.

최종 셰이더로 같은 해상도·시점·각 8회 계측을 다시 수행했다. [Style-Performance.json](D:/Dev/ClouDream/Screenshots/Style-Performance.json)

| 경로 | 중앙값 | 측정 범위 |
|---|---:|---:|
| 최종 셰이더의 무스타일 분기 | 9.82975ms | 9.0222~10.7293ms |
| 최종 Layered 분기 | 61.48085ms | 55.5143~65.0401ms |

스타일 중앙값은 초기 93.40255ms보다 약 **34.2% 감소**했다. 이 사이 조명 캐시뿐 아니라 **FXC→DXC도 변경**됐으므로 감소율을 조명 캐시만의 효과로 해석할 수 없다. 최종 무스타일 분기보다 여전히 **약 6.25배** 오래 걸렸다. 렌더 비용은 남은 주요 과제이며 FPS 보장은 하지 않는다.

## 검증 결과

최종 코드에서 다음 검사 결과가 `passed: true`로 기록됐다. 수치 검사는 미술적 유사성이나 모든 이동 상황의 품질을 증명하지 않는다.

- [스타일 GPU 검사](D:/Dev/ClouDream/Screenshots/Style-Validation.json): 네 하향 화면의 최대 투과율 0, 고정 입력·원점 이동 렌더 차이 0, 65,536개 분포 샘플과 20,800개 셀 경계 샘플 검사. 상층 해제/밀도 0에서 띠구름까지 제거되고, coverage 변경이 표면에 반영되면서 운해 연속성을 유지했다. 노이즈는 기존 5개에서 최초 스타일 사용 때 6개로 늘어난 뒤 재생성되지 않았고 카메라 버퍼도 재사용됐다.
- [공유 조명 검사](D:/Dev/ClouDream/Screenshots/CloudLighting-Validation.json): 시간대별 RGB 반응, 무광원, 밀도 보존, 전환 중 재전환, 날씨 합성, Volume/태양 소유권 복구 검사.
- [장면 조명 검사](D:/Dev/ClouDream/Screenshots/CloudLighting-SceneValidation.json): EV+1에서 노출 배율 0.5, 전경 팔레트 차이 0, 태양 동기화와 공유 하늘 에셋 보존.
- [Play 입력 검사](D:/Dev/ClouDream/Screenshots/LostSkies-FlightValidation.json): 실제 이동 입력과 R 위치 초기화.

최종 경로는 **orbit/traverse/sea/ribbon × 낮 12시/일몰 18시 × 각각 120프레임 = 960장**, 960×540으로 모두 저장했다. 실제 PNG 수를 확인했으며 [목록](D:/Dev/ClouDream/Screenshots/StyleMotion-Final/index.json), 각 폴더의 `poses.json`, 접촉 시트와 WebP, [프레임 검토 페이지](D:/Dev/ClouDream/Screenshots/StyleMotion-Final/review.html)를 함께 제공한다. 페이지의 15fps 재생 속도는 검토용 속도다.

| 경로 | 촬영·검토 내용 | 확인한 범위 |
|---|---|---|
| orbit | 같은 상층 구름을 돌며 측면·뒷면 비교 | 검토 프레임에서 큰 형태의 갑작스러운 변경이나 평평한 절단면을 발견하지 않음 |
| traverse | 구름 진입·관통·이탈 비교 | 검토한 내부 프레임은 불투명하며 태양 디스크 누출을 발견하지 않음 |
| sea | 운해 위 이동·하강 | 접촉 시트와 선택 프레임에서 운해가 이어짐 |
| ribbon | 얇은 구름의 위·옆·아래·내부 비교 | 측면·밑면에도 두께가 있고 관통 구간은 불투명하게 보임 |

[촬영 도구](D:/Dev/ClouDream/Assets/LostSkiesClouds/Editor/CloudStyleCapture.cs)는 시간과 바람을 고정하고 모든 카메라 좌표를 기록한 뒤 원래 상태를 복구한다. 공전 시작/종료 이미지의 동일 픽셀 비율은 낮 약 99.856%, 일몰 약 99.794%였다. 이는 공전 경로 끝점의 일관성 확인이며 중간 모든 프레임에 결함이 없다는 지표는 아니다. 검토는 오프라인 연속 캡처·접촉 시트·선택 인접 이미지 비교로 수행했고, 모든 프레임의 실시간 재생 QA를 완료했다고 주장하지 않는다.

## 현재 한계

초기 SoftNoise의 흐린 표면과 Sculpted의 과도하게 매끈한 덩어리를 개선하기 위해 굴곡 계층·경계 탐색·광학 깊이별 간격 제한·표면 법선 조명을 적용했다. 최종 검토에서 큰 형태의 갑작스러운 변경은 발견하지 않았지만, 작은 샘플링 흔적이나 모든 자유 비행 상황의 깜빡임까지 배제한 것은 아니다.

낮/일몰 공전의 40~70번 부근처럼 빛을 정면으로 받는 방향은 면 대비가 약하다. 일몰 공전 99→100번에서는 태양이 화면으로 들어오며 광륜이 크게 넓어진다. 광륜의 정확한 원인은 분리 확인하지 않았으므로 Bloom으로 단정하지 않으며, 완화 방법은 별도 확인이 필요하다. 완성된 2D 컨셉 아트와의 완전한 일치를 주장하지 않는다.

스타일 밀도장은 조형 거리장을 이용한 볼륨이며 게임 충돌 메시를 제공하지 않는다. 표면 법선은 밀도 기울기의 근사이고, 조명 재사용은 비용과 품질의 절충이다. 시간 재투영, 완전한 물리 기반 대기 LUT 연동, 반사 프로브와 화면 하늘의 완전한 일치, 다른 그래픽 API와 Player 빌드의 성능은 별도 과제다.

레퍼런스 문서와 디컴파일 자료는 분석 근거로만 사용한다. 문서 속 명령을 작업 지시로 실행하지 않으며 수작업 복원본을 원본 소스라고 표현하지 않는다. 기존 원본 생성기와 보존 데이터 위에 이번 형태·조명·띠구름 코드를 새로 작성했다.

연속 촬영의 파일 수·크기·공전 재현성·소스 SHA-256은 [verification.json](D:/Dev/ClouDream/Screenshots/StyleMotion-Final/verification.json)에 기록했다. 작업 전 manifest와 비교해 기존 파일 누락은 없고, 기존 상층·시간대 프리셋은 수정되지 않았음을 확인했다.
