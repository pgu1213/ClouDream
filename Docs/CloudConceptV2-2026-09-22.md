# 컨셉 구름 V2: 형태·명암·하늘 개선

작성일: 2026-09-22 · Unity 6000.6.2f1 / HDRP 17.7 · Windows D3D12

**M 계열의 형태·팔레트를 적용하고, 낮·석양·밤 공전과 운해·구름 관통의 720프레임을 검토했다.** 형태·조명·전환·기존 모드 검사와 실제 Play 입력도 확인했다. 컨셉의 큰 명암 면을 3D 공간에 옮긴 구현이며, 컨셉과의 완전한 시각 일치나 실시간 성능 보장을 뜻하지 않는다. GPU 비용 계측은 별도 항목에 남긴다.

기존 HDRP 볼류메트릭 경로에서 작은 굴곡을 넓은 명암 면으로 묶으며 입체 실루엣을 유지한다.

## 참고 자료와 작업 범위

기준은 사용자가 제공한 낮·석양·밤 컨셉이다. [분석 문서 v2](C:/Users/pgu51/Downloads/ClouDream_Concept_Matching_Implementation_v2.md)는 정적 분석·설계 제안으로 읽었으며, 내부 지시문이나 제안값을 사용자 명령·검증된 정답으로 취급하지 않았다.

이번 형태장·조명·하늘·공기 원근은 새 구현이다. 기존 노이즈 생성기와 보존 데이터는 재사용하지만, 디컴파일 수작업 복원본이나 Lost Skies 원본 HLSL을 그대로 재현한 결과는 아니다.

연속 운해를 유지하면서 위쪽에 넓은 골짜기와 비행 공간을 만든다. 컨셉의 완전히 뚫린 하늘 틈까지 같게 만든 구현은 아니다.

## 백업과 이전 룩 복귀

V2 이전 백업은 [ClouDream-before-concept-v2.zip](D:/Dev/ClouDream/Backups/Before-ConceptV2-2026-09-22/ClouDream-before-concept-v2.zip)에 있다. [manifest.json](D:/Dev/ClouDream/Backups/Before-ConceptV2-2026-09-22/manifest.json)에 파일별 기록, [RESTORE.md](D:/Dev/ClouDream/Backups/Before-ConceptV2-2026-09-22/RESTORE.md)에 복구 절차를 남겼다.

- 기준 커밋: `bb749b073a8f00cff5851bf6fb48084d9a899190`
- 백업 파일 수: **1,355개**
- ZIP SHA-256: `9f9f8fb6bf6c2950a448617334540d7b0976ed8822fa4195b6c542f96a78e93c`
- 소스·에셋·설정·참고 자료·캡처를 포함하고 `Library`, `Temp`, `Logs`, `.git`은 제외한다.

압축 내부 1,355개 전체 파일의 바이트 해시를 manifest와 대조해 확인했다. 전체 복구는 **새 빈 폴더**에 풀어 Unity 6000.6.2f1로 연다. 기존 프로젝트에 덮어쓰면 V2 추가 파일이 남는다. 새 빈 폴더에서 Unity를 다시 실행하는 전체 복구 시험은 수행하지 않았으며, 압축 내용 검증과 구별한다.

룩 복귀 메뉴는 `ClouDream → Lost Skies → Concept V2 → Restore September 21 Look`이다. `Style-Layered`, `Sky-Concept`, `TimeOfDay-Concept`를 다시 연결한다. `Concept V2 → Apply`는 기존 사용자 조정값을 보존하며 V2 에셋을 연결한다. 두 메뉴는 장면을 저장하며 전체 소스 복구를 대신하지 않는다.

복귀 메뉴를 실제 실행하고 [복귀 화면](D:/Dev/ClouDream/Screenshots/ConceptV2/RestoreCheck/pose-01-hour-12.png)을 촬영한 뒤, Apply로 V2 연결까지 확인했다. 같은 카메라·12시의 A 기준 화면과 RGB 최대·평균 차이는 모두 0이었다([복귀 검사](D:/Dev/ClouDream/Screenshots/ConceptV2-Restore-Validation.json)). 이 한 시점의 룩 복귀 검사를 전체 소스 복구·재실행 시험과 구별한다.

## 왜 이 원리를 채택했는가

**형태와 조명의 역할 분리.** 작은 굴곡까지 차폐·법선이 읽으면 각 로브가 독립된 점토처럼 보인다. 같은 월드 형태에서 실루엣·투과·내부용 밀도와 약한 변위만 남긴 조명 밀도를 파생한다. 세부를 유지하면서 넓은 그늘로 묶기 위한 선택이다.

**미터 단위 필드와 크기 제한.** [CloudConceptShapes.hlsl](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudConceptShapes.hlsl)은 타원체와 높이장을 미터 단위로 결합해 규모 불일치를 피한다. 경계 두께·변위·법선 간격을 대표 반경으로 제한해 작은 띠가 큰 타워용 변위에 파괴되지 않게 한다. 비균일 타원체와 변위의 근사이며 정확한 sphere tracing용 SDF는 아니다.

**연속 운해의 큰 지형 리듬.** 넓은 높이장·흐름 방향 능선·선택적 봉우리로 면과 골짜기를 구별하고 평탄부 굴곡은 약하게 한다. coverage는 위쪽 표면을 바꾸며 바닥 연속성을 유지한다. 밀도 검사에서 coverage 양끝과 네 하향 시점을 확인했다.

**큰 몸체와 종속된 중간 로브.** 타워·구름둑·군집에 세 큰 몸체와 두 어깨를 결합하고 렌더 실루엣에만 아홉 중간 로브를 붙인다. 큰 법선·조명은 공통 몸체를 읽는다. 성장축·무게중심·반경을 바꾸고 셀 경계 확장을 제한한다. 초기 눈사람 형태를 고치기 위해 몸체에 종속된 돌출을 도입했다.

**띠의 흐름과 두께.** 지역 흐름을 공유하며 높이·폭을 바꾸고 일부 층을 겹친다. 실제 두께의 밀도를 사용해 위·옆·아래 회전과 관통 때도 가림을 유지한다.

**방향성 팔레트와 하늘.** 큰 법선·광원 방향·차폐에서 팔레트 좌표를 만들고 주변광으로 그늘을 조절한다. 시간대 빛을 담은 팔레트에 태양색을 중복 곱하지 않는다. [CloudArtSky.hlsl](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudArtSky.hlsl)은 방향성 공기광을 디스크·halo·별과 분리해 전방위 주황 수평선 문제를 줄인다.

**달빛 밤 추가.** Day / Sunset / Twilight를 보존하고 MoonlitNight의 달 방향·색·세기·별을 추가했다. 태양 세기 조절만으로 해결되지 않는 밤을 위해 일반 오브젝트와 구름이 같은 대표광 상태를 사용한다. 네 시간 상태이며 달 위상·천문 시뮬레이션은 아니다.

**불투명도 가중 깊이의 공기 원근.** 기여도 `T × alpha`로 깊이를 누적하고 premultiplied RGB에 거리별 공기색을 섞는다. 투과율을 보존하며 구름 없는 광선에는 원근색을 추가하지 않는다. 다층 대기의 단일 대표 깊이 근사다. 선형화와 HDRP 노출은 한 번씩 적용한다.

공통 연결은 `CloudFormationProfile`과 `CloudEnvironmentSource`를 유지한다. 날씨 색의 상대 보정도 `TintLighting()`을 통해 계속 합성한다. V2는 새 `ConceptV2 = 4` 모드이며 기존 0~3의 의미를 조용히 바꾸지 않는 별도 경로다.

## 실험 결과와 M 선택

캡처는 생성 이미지가 아닌 Unity 카메라 렌더다. 비교 자료는 [ConceptV2 폴더](D:/Dev/ClouDream/Screenshots/ConceptV2)에 PNG와 입력 JSON으로 남긴다. 아래는 후보 탐색의 의미이며 모든 단계를 하나의 변수만 바꾼 과학적 대조 실험으로 해석하면 안 된다. 코드와 파라미터가 함께 바뀐 단계도 있다.

| 후보 | 확인한 내용과 판단 |
|---|---|
| A-Layered | 이전 V1 기준. 작은 로브별 명암과 반복 운해를 비교하는 대조군이다. |
| B-FirstV2 | 넓은 운해 면은 개선됐지만 타워가 눈사람처럼 단순해졌다. 낮은 어둡고 석양은 탈색됐으며 밤 별은 과밀했다. |
| C-RicherSilhouette | 변위를 늘려 표면은 풍부해졌지만 큰 눈사람 구조를 바꾸지 못했다. 거칠기 증가만으로 채택하지 않았다. |
| D-Palette / E-NeutralTone | 팔레트·노출과 톤매핑 영향을 비교했다. 같은 큰 형태 문제는 남았으므로 후처리만으로 해결하지 않았다. |
| F-GrownLobes | 종속 로브로 큰 몸체의 돌출과 겹침을 개선했다. 밝은 면의 뭉침과 일부 독립 로브 인상이 남았다. |
| G-BroadFaces / H-WarmFaces | 큰 법선 비중을 높이고 세부를 완화했다. 면은 넓어졌지만 너무 매끈한 풍선·점토 인상 때문에 최종 후보에서 제외했다. |
| I-DetailLighting / J-NoAerial | 세부 조명과 공기 원근을 분리하는 진단 대조다. 최종 미술 프리셋으로 채택하지 않았다. |
| K-Balanced | 큰 로브가 읽히지만 매끈함이 과해 독립된 점토 덩어리 인상이 강했다. |
| L-FinerHierarchy | 실루엣·원경·능선에서 작은 굴곡이 동시에 강해 다시 폼 질감으로 치우쳤다. |
| **M-FinalCandidate** | 큰 밝은 면과 그늘을 유지하면서 중간 굴곡을 되살렸다. F/K/L/M 중 선택하고 이후 720프레임의 3D 경로로 확인한 최종 설정 계열이다. |

[M의 측광 석양](D:/Dev/ClouDream/Screenshots/ConceptV2/M-FinalCandidate/pose-02-hour-18.png)은 큰 몸체 왼쪽의 밝은 면과 오른쪽 그늘이 유지되는지 평가한 장면이다. 단일 화면을 최종 컨셉 매칭의 증거로 사용하지 않는다.

I 실험 JSON의 조명 변위는 420이지만 `ApplyConceptSettings`에서 실제 200으로 제한된다. 따라서 I를 렌더·조명 필드가 완전히 같은 대조군으로 해석하면 안 된다. 별도의 최종 형태 고정 조명 검사는 GPU 결과로 확인했다.

M 촬영 입력의 주요 값은 운해 주기 24,000m, 높이 변화 1,400m, 굴곡 텍스처 주기 2,700m, 렌더 변위 360m, 조명 변위 25m, 미세 변위 100m, 큰 법선 혼합 0.68, 차분 최대 150m, 공기 원근 배수 0.45다. 텍스처 주기는 실제 봉우리 간격과 같지 않고 노이즈 생성기의 내부 scale도 영향을 준다. 런타임에는 대표 반경에 따른 상한이 추가 적용된다. 값은 보편적인 정답이 아니라 현재 월드 규모의 후보 설정이다.

## 사용과 비교 재현

[Presets 폴더](D:/Dev/ClouDream/Assets/LostSkiesClouds/Presets)의 `Style-ConceptV2`, `Sky-ConceptV2`, `TimeOfDay-ConceptV2`를 연결한다. Play는 **4 낮 / 5 석양 / 6 황혼 / 7 달빛 밤**, **T 시간 재생·정지**다. 밤을 끈 기존 에셋은 세 상태를 유지한다. 데모는 마지막 시각에서 멈추며 24시간 자정 순환은 제공하지 않는다.

[CloudConceptCapture](D:/Dev/ClouDream/Assets/LostSkiesClouds/Editor/CloudConceptCapture.cs)의 `CapturePose(label, pose, hour)`는 1280×720 PNG와 JSON을 저장한다. 같은 pose는 시간대마다 위치를 유지한다. 형태가 바뀌면 이전 내부 후보가 여전히 실제 내부인지 다시 검사한다. 시점별 기준은 JSON에 기록한다.

촬영 후 카메라·풍속·시간을 복원하고 기준 해시·원점·프로필을 JSON에 기록한다. Volume 기록은 혼합 완료된 카메라 stack이 아닌 구성값이다. 연속 촬영은 별도 폴더에 저장해 V1 증거를 보존한다.

## 최종 기능 검증

Unity MCP에서 최종 소스를 컴파일하고 셰이더 메시지·콘솔 오류 모두 0을 확인했다. [최종 검증·해시 기록](D:/Dev/ClouDream/Screenshots/ConceptV2-Final-Verification.json)에 소스·에셋·장면 52개의 SHA-256과 프레임 수를 남겼다. 저장 상태는 V2·12시·자동 진행 활성·Play 정지다. D3D12 공통 지원 검사를 포함하며, 아래 여섯 검사 결과는 모두 `passed: true`다. 밤·장면 조명·상층 분포 검사는 마지막 입력·복귀 확인 전 다시 실행했다.

| 검사 | 확인한 계약과 증거 |
|---|---|
| [ConceptV2 Geometry](D:/Dev/ClouDream/Screenshots/ConceptV2-Geometry-Validation.json) | GPU 유한값, 연속 운해, 상층 분포·셀 경계, 원점 이동, 형태 고정 조명, 무광원, 노이즈·타깃 재사용. |
| [Night](D:/Dev/ClouDream/Screenshots/CloudNight-Validation.json) | 네 상태, 중단·재전환, 황혼 단축키, 태양·달 방향·소유권 복원, 날씨 보정, 기존 387개 시각 샘플 차이 0. |
| [Legacy Style](D:/Dev/ClouDream/Screenshots/Style-Validation.json) | 기존 Style-Layered의 밀도·조명·원점·리소스 계약 보존. V2 전용 필드 분리 판정과는 별도다. |
| [Cloud Lighting](D:/Dev/ClouDream/Screenshots/CloudLighting-Validation.json) | 광원 색·환경광 반영, 전환 중단·완료, 날씨 밀도 유지, 프로필·외부 상태 복원. |
| [Lighting Scene](D:/Dev/ClouDream/Screenshots/CloudLighting-SceneValidation.json) | 노출 +1EV에서 비율 0.5, 전경 팔레트 차이 0, 하늘·태양 동기화, 공유 에셋 보존. |
| [Sky Clouds](D:/Dev/ClouDream/Screenshots/SkyClouds-Validation.json) | 상층 분포·크기 변화·빈 셀·경계, seed 변화, 원점 이동, 생성 전환 연속성. |

V2 큰 법선 A/B의 방사량 차이는 **0.7939453**, 투과율 차이는 **0**이다. 실제 가림을 보존한 채 조명만 조정됨을 확인했다. 네 하향 화면의 최대 투과율 0, 원점 이동 차이 0, 65,536개 분포·20,800개 경계 샘플을 확인했다. 타깃 할당 2회와 스타일 노이즈 생성 5→6→6으로 재사용 계약도 통과했다. 이 수치를 FPS나 시각적 유사도 점수로 해석하지 않는다.

실제 Play의 W 입력으로 **719.117m** 이동했고 R이 원래 위치를 복원했다([비행 검사](D:/Dev/ClouDream/Screenshots/LostSkies-FlightValidation.json)). 실제 임시 키보드 입력으로 7은 22시·달 활성·24,000lux·자동 진행 꺼짐, 6은 19.25시를 확인했다([입력 기록](D:/Dev/ClouDream/Screenshots/ConceptV2-Play-Input.json)). 이 단축키 검사는 전환 시간을 0으로 수행했고 6초 전환은 별도 컨트롤러 검사로 확인했다. 임시 입력 장치를 제거하고 전환 시간을 6초로 복원한 뒤 Play를 종료했다.

## 같은 시점과 3D 경로 검토

최종 비교는 pose 02의 같은 카메라에서 [낮 12시](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/pose-02-hour-12.png), [석양 18시](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/pose-02-hour-18.png), [황혼 19.25시](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/pose-02-hour-19.25.png), [밤 22시](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/pose-02-hour-22.png)를 촬영했다. [네 시간대 비교표](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/time-of-day.jpg)와 각 PNG 옆 JSON으로 색·명암과 입력을 비교할 수 있다.

[3D 검토 페이지](D:/Dev/ClouDream/Screenshots/ConceptMotion-Final/review.html)는 다음 **6경로 × 120장 = 720장**, 960×540 원본을 재생한다. [index.json](D:/Dev/ClouDream/Screenshots/ConceptMotion-Final/index.json)에 프레임 수·해상도, 각 경로의 `poses.json`에 위치·회전·시각을 남겼다. WebP와 접촉 시트도 함께 제공한다.

| 경로 | 검토한 공간 변화 |
|---|---|
| orbit-12 / orbit-18 / orbit-22 | 같은 구름의 낮·석양·밤 360도 공전. 밝은 면·그늘·실루엣과 배경 가림의 변화. |
| traverse-12 | 구름 접근·내부·관통 후 배경 회복. |
| sea-12 | 운해 횡이동·하강과 표면 아래 시야. |
| ribbon-12 | 띠의 위·옆·아래 공전 및 실제 두께 통과. |

720장 전체 접촉 시트를 검토하고 변화가 큰 구간은 인접 원본으로 확대했다. 검토 범위에서 명백한 형태 재생성·pop, 칼로 자른 단면, 시점에 따라 사라지는 평면, 갑작스러운 그림자 전환은 발견하지 못했다. 띠 f100–107에서는 화면이 점진적으로 가려진 뒤 청록 안개를 통해 배경이 회복된다. 석양 공전 f111–115에서는 태양이 중앙 구름 뒤로 이동하며 f114부터 디스크가 가려진다.

밤 공전에는 달 디스크가 화면 밖에 있어 별도 시점으로 확인했다. [달 검사 기록](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/moon-occlusion.json)은 22시·FOV 48도·카메라 정면을 달 방향에 맞춘 조건이다. [중심 가림](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/moon-occlusion-0.png)과 옆으로 1,600m 이동한 시점에서 디스크가 숨고, [3,200m 이동](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/moon-occlusion-2.png) 후 구름 밖에서 보인다. [고도 16,000m](D:/Dev/ClouDream/Screenshots/ConceptV2/Final/moon-clear.png)에서도 달 방향을 확인했다. 얇은 경계의 연속 투과는 정량 측정하지 않았다.

이는 시간·바람을 고정한 오프라인 카메라 경로 검토다. HTML의 약 15fps 재생 속도는 게임 FPS가 아니다. 접촉 시트·인접 프레임 확인과 실제 Play 입력 검사를 구별하며, 모든 위치에서의 미세 떨림·시간 변화 중 이동·임의의 투명 재질 합성까지 검증했다는 뜻은 아니다. 장면 검사는 불투명 전경의 팔레트 보존 범위다.

## 지원 플랫폼과 성능

현재 지원·검증 대상은 **Windows D3D12**다. DX11 FXC shader worker 실패가 발생해 `CloudRaymarch.compute`에 `#pragma use_dxc dx12`와 `#pragma only_renderers dx12`를 적용했다. **DX11은 현재 미지원**이다. 관련 API 문서: [DXC 선택](https://docs.unity.com/en-us/engine/6000.6/manual/materials-and-shaders/shaders/shader-troubleshooting/shader-reducing/shader-dxc-compiler), [렌더 API 제한](https://docs.unity.com/en-us/engine/6000.6/manual/materials-and-shaders/shaders/writing-custom/shader-writing/writing-shader-tags/sl-shader-compilation-apis).

**최종 GPU 시간 측정은 실패했다.** RTX 4080·D3D12·960×544 조건의 [최종 계측 기록](D:/Dev/ClouDream/Screenshots/ConceptV2-GPU-Performance.json)은 `unavailable`이다. 마커 등록용 dispatch와 SceneView 갱신을 추가한 세 번째 시도에서도 측정 dispatch 1회 후 601번의 Editor update 동안 유효 샘플이 0개였으며 정리 절차는 완료됐다. 기록의 -1은 미측정값이며 0ms나 빠른 실행을 뜻하지 않는다.

초기 파일의 네 유효 결과는 종횡비 1.04185로 최종 1.76471 조건과 다르고 내부 시점도 미완료여서 성능표·개선율에 사용하지 않았다. 최종 중앙값·p95와 실시간 FPS는 제시할 수 없다. 계측 대상도 전체 프레임이 아닌 raymarch dispatch였으므로 동기 캡처·readback 시간이나 기존 V1 수치로 대신하지 않는다. **노이즈 생성 6회 이후 캐시 및 렌더 타깃 재사용은 형태 검사에서 별도로 확인한 사실**이다.

## 남은 시각적 한계

**띠구름이 지나치게 매끈하다.** ribbon-12 f30–43의 넓은 아랫면은 단색 청록에 가깝고 f90의 근접 윗면도 부드럽다. 두께와 가림은 유지되지만 긴 점토·비행선 같은 인상이 남는다. 컨셉의 층진 붓자국과 중간 규모 굴곡을 더 선택적으로 넣을 여지가 있다.

**둥근 결절과 운해 능선의 리듬이 반복된다.** 순광에서 비슷한 크기의 로브가 모여 팝콘·점토처럼 보이는 각도가 있다. 운해의 둥근 골짜기·능선도 컨셉의 길고 방향성 있는 면보다 규칙적으로 읽힌다. 큰 몸체·중간 돌출·작은 굴곡의 대비를 지역별로 달리하는 개선이 필요하다.

**밤의 거리감과 밝기 차이가 부족하다.** 어두운 하늘에 비해 운해가 밝고 선명하며, 전경·중경·원경이 비슷한 푸른 계열로 묶인다. 역광 중심부 역시 넓은 단색 암부가 강하다. 낮·석양의 넓은 명암 묶임은 개선됐지만 모든 방향에서 컨셉의 색·깊이를 같게 만들었다고 판단하지 않는다.

큰 법선과 완화한 차폐는 미술 목적의 근사다. 대표 깊이 공기 원근은 다층 매질의 정확한 해가 아니고, 아트 하늘 배경과 HDRP 환경·반사 전체도 동일한 대기 모델은 아니다. 달 지형 텍스처·위상 시뮬레이션, 다른 그래픽 API, Player 성능은 이번 확인 범위에 포함하지 않는다.
