# 절반 해상도 구름과 깊이 기반 업샘플링 검증

2026년 10월 2일, 승인받은 저해상도 렌더링과 깊이 기반 업샘플링을 Unity HDRP CustomPass에 구현했다. **기본값은 기존 75% 해상도와 깊이 보정 ON**이다. F8에서 50%로 낮추면 계산 비용이 줄어들며, 전경 물체 위로 번지던 구름은 깊이 보정으로 억제한다. 다만 저해상도에서 놓친 먼 구름의 세부 형태까지 복원하지는 못한다. GTX 1660의 FHD 60 FPS 달성은 확인하지 못했다.

## 요구와 구현 원리

사용자가 승인한 범위는 절반 해상도 렌더링, 깊이 기반 재구성, 성능과 경계 품질 비교다. 유체 반응은 기존 결정대로 후속 작업이다. 구름 밀도 생성과 날씨 공급 계약은 이번 단계에서 변경하지 않았다.

[FluidNinja 분석 검토](D:/Dev/ClouDream/Docs/FluidNinja-Optimization-Review-2026-10-01.md)에서 구분한 대로, 입력 유체 필드의 축소와 화면 구름의 업샘플링은 다른 문제다. 이번 기능은 FluidNinja 복원 소스의 직접 이식이 아니다. 설치된 HDRP의 [VolumetricCloudsLowResolution](D:/Dev/ClouDream/Library/PackageCache/com.unity.render-pipelines.high-definition@dd309ef59526/Runtime/Lighting/VolumetricClouds/HDRenderPipeline.VolumetricCloudsLowResolution.cs)도 저해상도 계산과 업스케일 단계를 구분한다. 그 원리를 참고하여 ClouDream의 투과율·깊이 계약에 맞는 코드를 새로 작성했다.

기존 50% 해상도 선택 기능에 다음 재구성을 연결했다.

- 전체 해상도의 장면 깊이와 저해상도 광선이 실제 사용한 깊이를 비교한다. 주변 2×2 표본에서 같은 표면에 가까운 표본을 우선 보간한다.
- 대응 표면이 없을 때만 3×3 이웃에서 대체 표본을 찾는다. 저해상도에서 아예 놓친 얇은 물체는 구름의 첫 교차 거리와 전체 해상도 물체 깊이를 비교해 뒤쪽 구름을 제거한다. 물체 앞의 구름은 유지한다.
- 이미 불투명도가 곱해진 RGB, Concept V2의 깊이 모멘트와 투과율에 동일한 가중치를 적용한다. Compact와 Regular의 서로 다른 첫 교차 거리 채널을 구분한다.
- 깊이 복사와 저해상도 Raymarch의 대응을 유지한다. 기존 깊이 RT를 재사용하므로 추가 RT가 없으며, ON/OFF 전환도 RT를 재할당하지 않는다.
- OFF는 기존 선형 보간 경로다. 현재 프레임만 사용하며 과거 프레임 누적이나 재투영은 포함하지 않는다.

핵심 구현은 [CloudReconstruction.hlsl](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudReconstruction.hlsl)과 [CloudComposite.shader](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudComposite.shader)에 있다.

## FHD 성능 측정

RTX 4080, D3D12, Unity 6000.6.2f1, HDRP 17.7 Editor Play에서 Game 뷰를 1920×1080으로 고정했다. 정오·바람 0·FOV 68이며, 셀 캐시·지원 영역 제거·Compact는 ON, 빈 구간 제한·그림자 종료·공간 조명은 OFF다. 각 경우에 워밍업 20개와 유효 표본 48개를 수집했다. 검사 중 Volume을 Game 카메라로 한정하고 끝나면 원래 설정을 복원한다.

아래는 시점 고정 오류를 수정한 뒤 측정한 **Cloud.Total GPU p50 / p95(ms)**다. 깊이 복사, 캐시 준비, Raymarch와 합성을 포함한다. 전체 HDRP 프레임 시간은 아니다.

| 시점 | 100% 선형 | 75% 선형 | 75% 깊이 보정 | 50% 선형 | 50% 깊이 보정 |
|---|---:|---:|---:|---:|---:|
| home | 47.24 / 55.91 | 31.20 / 53.69 | 31.76 / 50.35 | 17.18 / 27.68 | 18.79 / 31.89 |
| side | 66.59 / 75.49 | 44.44 / 49.18 | 43.64 / 50.10 | 24.85 / 28.53 | 23.60 / 27.61 |
| inside candidate | 11.85 / 16.91 | 6.74 / 9.77 | 6.83 / 9.95 | 4.15 / 4.83 | 4.13 / 4.70 |

75% 깊이 보정 대비 50% 깊이 보정의 중앙값은 home 약 40.8%, side 약 45.9%, inside 약 39.6% 감소했다. 절감의 주원인은 해상도 감소다. 깊이 보정의 합성 비용은 약 0.05ms로, 선형 합성 약 0.03ms보다 약간 높았다. 같은 해상도의 Raymarch에도 순차 측정 편차가 남으므로 ON/OFF의 전체 시간 차이를 모두 재구성 비용으로 해석하지 않는다.

75%의 실제 구름 버퍼는 1440×816, 50%는 960×544다. Compact 카메라 버퍼는 각각 약 17.93 MiB와 7.97 MiB다. 컴퓨트 정렬 때문에 세로 길이는 이상적인 배율에서 조금 올림된다.

FrameTimingManager가 반환한 Editor 전체 GPU 시간이 구름 패스보다 작아 전체 프레임 검증에 사용할 수 없었다. GPU 마커별 표본도 서로 프레임 정렬된 값이 아니므로 중앙값을 더해 총시간을 만들지 않는다. 설정창 Whole frame에는 Editor와 VSync 등이 포함된다. RTX 4080의 결과를 GTX 1660 FPS로 환산하지 않는다.

[시점 고정 후 원시 표본](D:/Dev/ClouDream/Screenshots/Reconstruction-SceneFrames.json)

조건 순서를 뒤집은 재측정에서도 같은 위치·회전이 유지됐다. 다음은 깊이 보정을 켠 두 해상도의 Cloud.Total p50 / p95(ms)다.

| 시점 | 75% 깊이 보정 | 50% 깊이 보정 | p50 감소 |
|---|---:|---:|---:|
| home | 32.83 / 37.51 | 17.28 / 29.37 | 47.4% |
| side | 44.10 / 54.66 | 25.60 / 28.65 | 41.9% |
| inside candidate | 6.72 / 9.74 | 4.42 / 4.87 | 34.2% |

정·역순 두 실행에서의 감소 범위는 약 34~47%다. p95 편차가 크므로 이를 고정적인 FPS 향상률로 해석하지 않는다. [역순 원시 표본](D:/Dev/ClouDream/Screenshots/Reconstruction-SceneFrames-Reverse.json).

### 비교 도구 오류의 정정

초기 비교에서 `DescribePath`를 매 옵션마다 호출했다. 이 함수는 현재 카메라에서 가까운 구름과 방향을 사용하므로 side·inside의 위치와 회전이 달라졌다. 초기 내부 54.4% 감소와 side 수치를 동일 시점의 개선율로 사용할 수 없다. 고정 home에서 경로를 한 번만 생성하고 모든 옵션이 같은 위치·회전을 재사용하도록 수정했으며, 측정 도중 카메라 이동도 검사한다. 최종 JSON의 각 시점별 위치·회전은 각각 하나이고 `comparisonPosesFixed`가 true임을 확인했다.

같은 도구를 사용한 이전 구간 제한·공간 조명 보고서에도 정정을 추가했다. 과거 독립 dispatch와 수치 정확성 검사는 별도 도구다. 잘못된 초기 원시 자료는 `Screenshots/*-InvalidPoseComparison.json`으로 보존하되 최종 성능 판단에 사용하지 않는다.

## 전경 경계와 구름 세부 품질

알려진 GPU 입력으로 상수 보존, 전경과 하늘의 경계, 저해상도에서 놓친 얇은 전경, 전경 앞 구름의 유지 등 4조건을 Compact와 Regular 양쪽 계약으로 검사했다. **8개 조건이 통과했고 최대 채널 오차는 2.98×10⁻⁸**이었다.

실제 HDRP 장면은 100% 선형 출력을 기준으로 home·side·inside·horizon·quick-turn·sunset·foreground의 7조건에서 75% 선형, 50% 선형, 50% 깊이 보정을 비교했다. 640×360과 FHD에서 각각 21개 비교를 실행했다. quick-turn은 회전 직후의 정지 시점 검사이며 연속 이동 영상 검사를 뜻하지 않는다.

FHD 전경 검사에는 넓은 사각형과 화면 폭 약 1.4픽셀·3픽셀의 얇은 물체를 300m 앞에 배치했다. 기준 이미지에서 확인된 전경 182,886픽셀의 평균 최대 RGB 오차는 다음과 같다.

| 방식 | 전경 평균 오차 |
|---|---:|
| 75% 선형 | 0.002334 |
| 50% 선형 | 0.010474 |
| 50% 깊이 보정 | 0.00000194 |

이 전경 조건에서는 50% 선형 대비 오차가 약 99.98% 줄었다. [선형 합성](D:/Dev/ClouDream/Screenshots/Reconstruction-FHD/foreground-1.png)과 [깊이 보정](D:/Dev/ClouDream/Screenshots/Reconstruction-FHD/foreground-2.png)의 얇은 물체 가림도 확인했다. 모든 장면이나 투명 물체의 경계를 같은 비율로 개선한다는 의미는 아니다.

반면 구름끼리의 윤곽에는 장면 깊이 경계가 없으므로 세부 복원 효과가 거의 없었다. FHD home의 강한 색 경계 평균 오차는 75% 선형 0.014652, 50% 선형 0.020433, 50% 깊이 보정 0.020425다. sunset에서는 각각 0.025924, 0.036759, 0.036761이다. 50%는 비용을 줄이는 대신 먼 윤곽과 얇은 구름의 선명도를 낮춘다. 이 때문에 기본 해상도를 강제로 50%로 낮추지 않았다.

오차는 후처리와 안티앨리어싱을 끈 노출 후 선형 HDR 값이다. 각 픽셀의 최대 RGB 차이를 평균하며, 경계는 기준 이미지의 인접 픽셀 차이가 0.015보다 큰 픽셀이다. 진단 PNG에는 고정 Reinhard 표시 변환을 적용했다. 최종 게임의 톤매핑 영상이나 사용자 승인 품질 기준과 같지 않다. 재구성은 저해상도 광선에 없는 정보를 생성하지 못하며, 전경이 구름 내부를 가르는 경우의 부분 적분을 정확히 재계산하지는 않는다.

[FHD 품질 수치](D:/Dev/ClouDream/Screenshots/Reconstruction-FHD-Validation.json), [축소 검사와 회귀 판정](D:/Dev/ClouDream/Screenshots/Reconstruction-Validation.json), [100% 진단 이미지](D:/Dev/ClouDream/Screenshots/Reconstruction-FHD/home-100.png), [50% 깊이 보정 이미지](D:/Dev/ClouDream/Screenshots/Reconstruction-FHD/home-2.png).

## Play 설정과 검증

Play에서 **F8 → 50% / Fast**를 선택하고 **Depth-aware upsampling (foreground edges)**을 켜면 이번 조합을 사용할 수 있다. 깊이 보정만 끄면 동일 해상도에서 경계 차이를 비교할 수 있다. Restore session defaults는 75%·깊이 보정 ON을 포함한 실행 시작 설정을 복원한다. 변경은 현재 Play 실행에만 적용된다.

실제 Play의 **26회 설정 전환**, F8 열기·Esc 닫기, 비행 입력 중단·복구, 깊이 보정 전환 중 RT 재할당 없음과 시작 설정 복원이 통과했다. 기존 공간 조명 설정과 자원 반환 검사도 통과했다. 깊이 보정 ON에서 기존 HDRP 노출 검사의 EV+1 배율은 정확히 0.5, 전경의 하늘 팔레트 차이는 0이었다. 하늘·태양 동기화와 공유 에셋 보존도 통과했다.

[Play 검사](D:/Dev/ClouDream/Screenshots/Optimization-PlayValidation.json), [노출과 전경 합성 검사](D:/Dev/ClouDream/Screenshots/Reconstruction-LightingValidation.json), [실제 FHD 설정창](D:/Dev/ClouDream/Screenshots/Optimization/Reconstruction-F8-FHD.png). 설정창의 새 옵션과 닫기 버튼이 잘리지 않는 것도 확인했다.

최종 Unity MCP 컴파일 확인과 Console 오류·경고 0건, Git 공백 검사 통과를 확인했다. 검증 뒤 Play를 종료하고 Game 뷰를 기존 Free Aspect로 복원했다. [기존 크기에서의 설정창](D:/Dev/ClouDream/Screenshots/Optimization/Reconstruction-F8-FreeAspect.png)도 확인했다. 장면에는 75%·깊이 보정 ON·공간 조명 OFF를 저장했다.

Unity 메뉴 **ClouDream → Lost Skies → Optimization**에서 Validate Reconstruction, Validate FHD Reconstruction으로 재검사한다. Measure FHD Reconstruction과 Measure FHD Reconstruction Reverse는 FHD Game 뷰와 Play가 필요하다. 후자는 조건 순서를 뒤집는다. 측정 도구의 최신 버전은 기본값 검증을 위해 75% 깊이 보정도 함께 측정한다. 원시 JSON과 PNG는 `Screenshots`에 저장되며 Git에서 제외될 수 있다.

목표 GPU의 Player 전체 프레임 측정과 장시간 비행 중 윤곽 안정성 확인은 남아 있다. 현재 장비에서도 일부 시점은 구름 패스만 16.67ms를 넘으므로 이번 변경만으로 FHD 60 FPS 목표를 충족했다고 판단하지 않는다.
