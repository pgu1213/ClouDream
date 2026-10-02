# 팔레트 전용 조명 연산 생략 도입 결과

2026년 10월 2일. 사용자의 “여러 개를 한 번에 하지 말고 한 기능씩 도입” 요청에 따라 **최종 출력에 사용되지 않는 물리 조명 계산 생략 한 가지**를 구현했다. **기본값은 OFF이며 F8의 실험 옵션으로 제공한다.** 화질 오차 검사는 통과했지만 반복 GPU 측정에서 일관된 성능 향상이 확인되지 않아 기본 최적화로 채택하지 않았다. GTX 1660, FHD 60 FPS 달성을 입증한 결과도 아니다.

## 적용 조건과 동작

기존 Concept V2는 물리 조명과 팔레트 조명을 모두 계산한 뒤 혼합한다. 팔레트 비중이 100%이면 물리 조명의 결과가 최종 색에 기여하지 않는다. 이 경우 전용 컴퓨트 셰이더 변형에서 물리 조명 코드를 제외한다.

- 사용자가 옵션을 켰고, 하늘 조명이 활성화됐으며, 팔레트 비중이 1 이상이고, 형태 프로필이 지원을 선언했을 때만 활성화한다. 기존 셰이더 입력의 Clamp01과 같은 경계다.
- `CloudFormationProfile.SupportsPaletteLightingFastPath`의 기본값은 false다. 현재는 `CloudStyleProfile`의 Concept V2만 true를 반환한다. 혼합 비중 0.9999, 하늘 조명 OFF, 다른 구름 형태는 기존 계산을 사용한다.
- 조건은 매 렌더 다시 평가한다. F8의 `Palette-only active`는 사용자 체크 상태와 별개로 실제 선택된 경로를 표시한다.
- 밀도, 광선 표본, 자기 차폐, 법선, 투과율과 깊이 데이터는 유지한다. 추가 RT나 시간 누적 버퍼가 없다.
- 새로운 셰이더 키워드가 변형 수를 늘린다. Editor에서 아직 준비되지 않은 조합을 처음 사용할 때 셰이더 컴파일 지연이 발생할 수 있다. 이번 검사에서도 최초 컴파일 대기가 있었다.

핵심 코드는 [CloudRaymarch.compute](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute), [LostSkiesCloudRenderer.cs](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/LostSkiesCloudRenderer.cs), [CloudFormationProfile.cs](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudFormationProfile.cs)에 있다. 기존 Unity 구름의 실제 계산 경로를 분석해 새로 작성한 구현이며, FluidNinja 원본이나 수작업 복원본을 직접 이식한 기능은 아니다.

## 화질과 데이터 계약 검사

작업 시작 전에 Unity AssetDatabase로 복사한 변경 전 컴퓨트 셰이더를 독립 기준으로 사용했다. 20개 조건 × Regular/Compact 2형식 × OFF/ON/OFF 3상태의 **120개 비교가 통과**했다. 320×184 해상도이며, 기준 영상에서 구름이 존재하는 픽셀은 조건 전체 합계 1,564,280개다.

| 항목 | 결과 |
|---|---:|
| 최대 RGB 절대 오차 | 0.0009765625 |
| 조건별 평균 RGB 오차 중 최댓값 | 9.54×10⁻⁸ |
| 최대 투과율 오차 | 0 |
| 최대 깊이 모멘트 오차 | 0 |
| 최대 첫 구름 교차 거리 오차 | 0 |
| OFF 및 OFF 복귀와 변경 전 기준의 RGB 오차 | 0 |
| 옵션 전환에 따른 RT 재할당 | 없음 |

ON의 미소한 RGB 차이는 부동소수점 계산 경로 변경에 따른 반올림 규모이며, 비트 단위 동일 출력으로 표현하지 않는다. 허용한 최대 RGB 절대 오차는 0.001이다. 이 표본 검사는 모든 장면에서 오차 상한을 보장하는 수학적 증명은 아니다.

수평선, 상공, 내부, 음수 셀, 축 평행 광선, 최대 변위, 원점 이동, 낮은 태양, 밝은 테두리, 야간, 혼합 경계, 미지원 형태와 그림자·공간 조명 조합을 포함했다. 혼합 비중 0/0.5/0.9999, 하늘 조명 비활성, SoftNoise와 LayeredBillows는 기존 출력과 정확히 일치했다.

[수치 비교 원시 결과](D:/Dev/ClouDream/Screenshots/PaletteLighting-Validation.json), [재실행 도구](D:/Dev/ClouDream/Assets/LostSkiesClouds/Editor/CloudPaletteLightingValidation.cs). 기본 메뉴 실행은 현재 셰이더의 OFF/ON/OFF를 비교한다. 최초 도입 검사는 별도 변경 전 자산을 `RunAgainst`에 전달했다.

변경 전 셰이더 보관본: [CloudPaletteBaseline.compute.txt](D:/Dev/ClouDream/Screenshots/CloudPaletteBaseline.compute.txt). SHA-256은 `F190FB71E74C75B6EF7CD54A91C7D1B0E39D106068372EFF13A5A3944138E2D9`이다. 보고서의 referenceShader 경로는 검사 당시 임시 Unity 자산 경로이며, 제품 자산에서는 검사 후 제거했다. 보관본의 상대 include는 원래 Runtime 폴더를 기준으로 한다.

## 실제 Game 카메라 GPU 측정

RTX 4080, D3D12, Unity 6000.6.2f1, FHD Game 뷰, 구름 75%, 깊이 보정 ON. 정오·바람 0·FOV 68이며, 셀 캐시·지원 영역 제거·Compact는 ON, 구간 제한·그림자 종료·공간 조명은 OFF다. 고정된 세 위치·회전에서 OFF/ON/ON/OFF와 역순 ON/OFF/OFF/ON을 비교했다. 각 경우 워밍업 20개와 유효 표본 48개를 수집했다.

아래는 각 반복의 **Cloud.Total GPU p50 / p95(ms)**다. 구름 패스의 시간이며 전체 게임 FPS가 아니다. 마지막 역순 결과에는 요청 상태와 실제 `paletteFastPathActive`가 모두 일치함을 기록했다. 정순은 그 진단 필드를 추가하기 전 실행이다.

| 실행 | 시점 | OFF 1 | OFF 2 | ON 1 | ON 2 |
|---|---|---:|---:|---:|---:|
| 정순 반복 | home | 31.40 / 51.88 | 31.92 / 51.75 | 31.28 / 49.01 | 32.24 / 50.43 |
| 정순 반복 | side | 43.26 / 47.64 | 43.77 / 47.23 | 28.57 / 42.72 | 28.89 / 45.38 |
| 정순 반복 | inside | 6.93 / 9.94 | 6.83 / 9.64 | 6.81 / 9.76 | 6.83 / 9.74 |
| 최종 역순 | home | 34.99 / 38.58 | 36.03 / 39.40 | 31.77 / 51.18 | 31.68 / 48.65 |
| 최종 역순 | side | 43.38 / 47.75 | 44.13 / 49.49 | 43.38 / 50.21 | 41.30 / 48.10 |
| 최종 역순 | inside | 9.69 / 10.16 | 9.70 / 10.04 | 9.64 / 10.25 | 9.64 / 10.04 |

정순의 side에서는 약 34% 감소했지만 최종 역순에서는 같은 수준의 감소가 재현되지 않았다. home도 중앙값 감소와 p95 증가가 함께 나타났으며, inside의 실행 간 절대 시간도 달라졌다. 따라서 특정 실행의 가장 좋은 값을 대표 향상률로 채택하지 않는다. Editor 실행 편차와 GPU 상태 영향을 분리해 확인하지 못했으며, GTX 1660 Player 실측은 남아 있다.

초기 전용 변형 검사에서도 home ON 중앙값이 약 49ms로 OFF 약 31ms보다 나빴던 표본이 있었다. 초기 자료를 삭제하지 않고 보존했다. 최초 시도한 동적 분기 방식 역시 home·inside 성능이 악화돼 최종 구현에서 제거했다. 현재 구현은 셰이더 변형 방식만 사용한다.

- [정순 반복 원시 결과](D:/Dev/ClouDream/Screenshots/PaletteLighting-SceneFrames.json)
- [최종 역순 원시 결과](D:/Dev/ClouDream/Screenshots/PaletteLighting-SceneFrames-Reverse.json)
- [전용 변형 최초 정순](D:/Dev/ClouDream/Screenshots/PaletteLighting-Specialized-First-SceneFrames.json), [최초 역순](D:/Dev/ClouDream/Screenshots/PaletteLighting-Specialized-First-SceneFrames-Reverse.json)
- [채택하지 않은 동적 분기 정순](D:/Dev/ClouDream/Screenshots/PaletteLighting-DynamicBranch-SceneFrames.json), [역순](D:/Dev/ClouDream/Screenshots/PaletteLighting-DynamicBranch-SceneFrames-Reverse.json)

## Play 설정창

F8 → `Skip unused physical lighting (experimental)`로 켜고 끈다. 아래 `Palette-only active`가 true일 때만 전용 경로가 실제 사용된다. `Restore session defaults`는 Play 시작값을 복구하고 `Optimizations OFF / compare`는 이 옵션도 끈다. 기본값은 OFF다.

Unity MCP를 통한 실제 Play 검사에서 **36회 설정 전환**, F8 열기와 Esc 닫기, 비행 입력 중단·복원, 팔레트 옵션의 RT 재할당 없음과 시작값 복원이 모두 통과했다. 기존 공간 조명 자원 해제와 깊이 보정 전환도 함께 통과했다. 컴파일 및 Play 검사 뒤 Unity Console 오류·경고는 0개였다. FHD 설정창에서 새 체크박스, 활성 상태와 하단 닫기 버튼까지 표시됨을 이미지로 확인했다.

[Play 검증 결과](D:/Dev/ClouDream/Screenshots/Optimization-PlayValidation.json), [설정창 캡처](D:/Dev/ClouDream/Screenshots/Optimization/Palette-F8-FHD.png). 화면의 Whole frame 값은 Editor 실행 간격이며 위 GPU 벤치마크 수치와 구분한다.

현재 단계는 이 기능 하나의 도입과 검증까지다. 시간 재투영, 거리별 LOD와 추가 빈 공간 건너뛰기는 이번 변경에 포함하지 않았다.
