# 시간대별 하늘과 구름 조명

작성: 2026-09-21 · ClouDream · Unity 6000.6.2f1 / HDRP 17.7

기존 운해와 상층 구름의 밀도 함수·배치·시드는 유지하고, 낮·일몰·해질녘의 하늘과 구름 조명을 연결했다. 따뜻한 직접광, 청록·보라 주변광, 태양을 향한 가장자리 산란을 조정할 수 있다. 구름의 표면 형태를 스타일라이즈하는 작업은 다음 단계다.

비교 캡처: [낮](D:/Dev/ClouDream/Screenshots/Lighting-Day.png) · [일몰](D:/Dev/ClouDream/Screenshots/Lighting-Sunset.png) · [해질녘](D:/Dev/ClouDream/Screenshots/Lighting-Twilight.png)

## 실행과 조절

[LostSkiesCloudSea 장면](D:/Dev/ClouDream/Assets/LostSkiesClouds/Scenes/LostSkiesCloudSea.unity)에서 Play한다. 시간대 값은 [TimeOfDay-Lighting.asset](D:/Dev/ClouDream/Assets/LostSkiesClouds/Presets/TimeOfDay-Lighting.asset), 장면 연결과 재생 속도는 `CloudTimeOfDayController`에서 조절한다.

| 기준 | 시각 | Play 단축키 | 조명 방향 |
|---|---:|---|---|
| 낮 | 12:00 | 4 | 청록 하늘, 따뜻한 백색광 |
| 일몰 | 18:00 | 5 | 주황 직접광, 보라 계열 그늘 |
| 해질녘 | 19:15 | 6 | 직접광을 줄이고 청색·보라 주변광 유지 |

4/5/6은 자동 재생을 끄고 해당 기준까지 기본 6초 동안 전환한다. T는 시간 재생/일시 정지, H는 안내 표시 전환이다. 자동 재생은 기본 180초에 12:00부터 19:15까지 진행하며 끝에서 멈춘다. 끝에 도달한 뒤 T로 재생을 켜면 낮부터 시작한다. **현재는 세 시간대의 비교 구간이며 24시간 순환·새벽·달빛 시스템은 아니다.** [입력 구현](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudTimeOfDayInput.cs:22)

편집 중에는 컨트롤러의 `Preview/Day`, `Preview/Sunset`, `Preview/Twilight` 메뉴로 즉시 확인한다. 코드에서 즉시 변경은 `SetTime(hours)`, 부드러운 변경은 `TransitionTo(hours, seconds)`를 사용한다. 전환 중 재호출하면 현재 시각부터 새 목표로 이어진다.

## 무엇을 연결했고 왜 필요한가

| 구현 | 필요성과 코드 근거 |
|---|---|
| 공유 조명 상태 | 하늘과 구름이 서로 다른 시간대를 표현하지 않도록 `CloudLightingProfile.Evaluate()`가 각도·색·강도·노출을 함께 보간한다. [프로필](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudLightingProfile.cs:31) |
| HDRP 태양·하늘·노출 연결 | 같은 상태로 Directional Light 방향/색/Lux, PhysicallyBasedSky의 지평선/천정 틴트, Fixed Exposure를 갱신한다. 기존 물리 기반 하늘과 태양 디스크를 계속 사용한다. [장면 적용](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudTimeOfDayController.cs:163) |
| 색을 보존하는 구름 적분 | 조명을 명도로 바꿔 두 색으로 다시 칠하던 경로 대신, 직접광과 하늘 주변광의 RGB를 밀도 안에 적분한다. 직사광은 Lux에 비례한 배수와 지평선 감쇠를 적용한다. [GPU 입력](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/LostSkiesCloudRenderer.cs:198), [산란 계산](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute:168) |
| HDRP 노출과 합성 | 구름의 상대 방사량에 고정 보정값 3000과 환경 밝기를 곱한 뒤 HDRP 노출을 한 번 적용한다. 이 보정값은 물리 측정값이 아니라 현재 장면의 밝기 기준이다. [합성](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudComposite.shader:36) |

하늘의 컨셉 색을 조절하기 위해 천정·지평선·하부의 팔레트도 추가했다. 실제 장면 깊이가 없는 배경 픽셀에만 HDRP 하늘과 섞고, 구름의 투과율만큼 뒤에 보이게 한다. 불투명 물체 위에는 하늘 팔레트를 덮지 않는다. 기본 `skyBlend`는 낮 0.45, 일몰 0.94, 해질녘 1이다. 1에 가까울수록 팔레트 비중이 커지고 HDRP 하늘/태양의 기여는 줄어든다. [배경 계산](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudComposite.shader:21)

주변광만 남는 해질녘에도 굴곡이 읽히도록 밀도가 있는 레이 샘플마다 위쪽 60m·180m·450m의 밀도를 추가 조회한다. 이 세 조회로 위를 덮는 구름의 양을 근사하여 주변광을 줄인다. 기존 태양 방향 조회 7회에 주변광용 조회 3회가 추가되며, 세부 침식 노이즈는 생략한다. 빈 레이 구간에서는 이 조명 계산을 하지 않는다. **조회 횟수 증가가 전체 GPU 시간의 같은 비율 증가를 뜻하지는 않으며, 실제 비용은 장면·해상도별 프로파일링이 필요하다.** [주변광 차폐](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute:168)

## 날씨·바이옴 확장과 수명 관리

`CloudTimeOfDayController`는 기존 `CloudEnvironmentSource`를 상속하고 `weatherSource`를 감싼다. 먼저 날씨/바이옴의 밀도·상층 밀도·coverage·밝기를 얻고, 그 결과에 시간대 조명을 결합한다. 형태는 기존 `CloudFormationProfile` 계약을 그대로 사용한다. 시간 변경 때문에 노이즈를 다시 생성하거나 구름 배치를 바꾸지 않는다. Play 중 기존 바람 이동은 계속된다.

기존 날씨 색도 재사용한다. `TintLighting()`은 ClearDay 기본색을 중립으로 삼아 `sunlight`와 `highlightTint`를 직접광 색 배수로, `shadowTint`를 주변광 색 배수로 바꾼다. 따라서 맑은 날은 시간대 룩을 유지하고, 기존 날씨/바이옴 색은 구름에 추가 반영된다. 이 색 보정은 구름의 재질 반응이며 전역 태양과 하늘을 다시 물들이지 않는다. [환경 합성](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudEnvironment.cs:57)

시간대 컨트롤러는 공유 Volume 에셋 대신 자체 복제본을 소유한다. 비활성화하거나 조명 프로필을 제거하면 원래 Volume 연결과 태양 상태를 복구하고 복제 리소스를 해제한다. 실행 중 다른 시스템이 Volume 프로필을 교체하면 새 프로필을 기준으로 다시 준비하며, 종료 시 그 외부 프로필을 보존한다. `sharedProfile` 없이 실행 중 생성한 `profile`만 있는 Volume도 지원한다. 조명 프로필이 없으면 날씨 공급자의 결과를 그대로 통과시킨다. [소유권 처리](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudTimeOfDayController.cs:203)

## 검증 자료

Unity MCP로 컴파일과 실제 GPU/Play 경로를 확인했다. 최종 콘솔의 오류·경고는 0건이다. 아래 검사는 통과했으며 FPS나 GPU 시간은 측정하지 않았다.

| 확인 항목 | 결과 |
|---|---|
| 세 시간대의 구름 투과율·깊이 | 최대 차이 0, 형태 유지 |
| 직접광·주변광을 모두 끈 GPU 출력 | 최대 RGB 0 |
| HDRP 노출 EV +1 | 노출 배율 정확히 0.5 |
| 배경 팔레트가 불투명 전경에 미치는 영향 | 후처리를 제외한 깊이 마스크 검사에서 픽셀 차이 0 |
| 운해·상층 회귀 검사 | 하향 최대 투과율 약 0.008, 분포·원점 이동·캐시 검사 통과 |
| 전환과 리소스 수명 | 시간/날씨 재전환, 자동 재생 종료, Volume 교체·복구 통과 |
| Play 입력 | 4→12:00, 5→18:00, 6→19:15 도달, T로 시간 진행 재개, H로 안내 숨김 |

전경 검사는 조명·블룸의 간접 영향을 제외하기 위해 임시 Unlit 물체와 후처리 없는 카메라 설정을 사용했고, 종료 후 원래 상태로 복구했다. 실제 낮·일몰·해질녘 캡처는 정상 렌더 설정에서 확인했다.

- [CloudLighting-Validation.json](D:/Dev/ClouDream/Screenshots/CloudLighting-Validation.json): 시간대 간 투과율/깊이 유지, RGB 반응, 무광원, 주변광, 전환 연속성, 날씨 합성, Volume/태양 복구.
- [CloudLighting-SceneValidation.json](D:/Dev/ClouDream/Screenshots/CloudLighting-SceneValidation.json): 실제 HDRP 노출, 배경 팔레트의 전경 가림, 태양 동기화, 공유 하늘 에셋 보존.
- [SkyClouds-Validation.json](D:/Dev/ClouDream/Screenshots/SkyClouds-Validation.json): 기존 상층 분포·경계·원점 이동·버퍼 재사용 검사.
- [LostSkies-GpuValidation.json](D:/Dev/ClouDream/Screenshots/LostSkies-GpuValidation.json): 기존 운해의 반복 렌더와 하단 연속성 검사.

메뉴 **ClouDream → Lost Skies → Validate Time Of Day Lighting**에서 조명 검사를 실행한다. **Capture Lighting References**는 동일 카메라의 세 시간대 이미지를 저장한다. 에디터 연결·컴파일·Play 검증은 Unity MCP로 진행한다.

## 구현 범위와 남은 한계

이번 시간대 시스템과 조명 코드는 ClouDream에서 새로 작성했다. Lost Skies 보강 자료의 수작업 복원본을 원본 소스라고 보거나, 원본 조명·대기 렌더러를 그대로 복사한 구현으로 설명하지 않는다. 이전 자료 채택 근거는 [상층 구름 보고서](D:/Dev/ClouDream/Docs/CloudSystem-Adoption-2026-09-21.md)에 별도로 남아 있다.

구름 주변광은 시간대별 색과 국소 밀도 차폐를 이용한 미술적 근사이며, HDRP나 원본 게임의 대기 산란 LUT를 직접 샘플링하지 않는다. 화면의 팔레트 합성은 HDRP 하늘 큐브맵에 반영되지 않으므로 반사 프로브·환경광에서 보이는 하늘과 카메라 배경이 다를 수 있다. 완전한 물리 기반 에너지 일치나 전역 구름 그림자도 이번 범위가 아니다.

큰 덩어리·중간 굴곡·매끈한 표면을 정리하는 스타일라이즈 형태 작업, 시간 재투영, 다른 그래픽 API와 Player 빌드 검증은 후속 과제로 남는다.
