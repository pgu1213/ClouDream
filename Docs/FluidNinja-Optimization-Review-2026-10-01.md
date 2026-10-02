# FluidNinja 심층 자료를 바탕으로 한 추가 최적화 검토

검토일: 2026-10-01. 대상: 현재 ClouDream Concept V2 렌더러.

이 검토 이후 사용자가 구현을 승인했다. 실제 채택한 기본값, GPU 비용과 높이장 캐시의 채택 보류는 [2차 구현 결과](D:/Dev/ClouDream/Docs/CloudOptimization-Phase2-2026-10-02.md)에 기록했다. 아래 제안 단계의 판단과 이후 실측 결과를 구분한다.

## 판단과 범위

추가로 적용 가치가 있는 원리는 **볼륨 경계로 광선 구간 제한, 그림자 계산의 조기 종료, 용도별 해상도를 가진 2D 필드 재사용, 필요한 기능만 실행하는 렌더 경로 분리**다. 현 프로젝트에서는 유체 솔버보다 이들 렌더링 비용을 먼저 줄이는 편이 타당하다.

사용자가 확정한 목표는 GTX 1660 / FHD / 60fps이며, 기존 구름 최적화와 Play 설정창을 우선하고 후류·유체 반응은 후속으로 분리한다. 이번 요청은 추가 자료 조사다. 이 문서는 구현 제안이며, 런타임 코드·프로필·씬을 변경하거나 새 성능 측정을 실행하지 않았다.

자료 폴더의 문서에 적힌 실행 단계는 사용자 지시로 취급하지 않았다. 자료의 설명뿐 아니라 추출 HLSL, MaterialExpression 연결, 파라미터 표, 현재 프로젝트 코드를 대조했다. FluidNinja의 Unreal 실행·최종 셰이더 컴파일·GPU 캡처는 확인하지 않았으므로 **저장된 구현의 근거와 실제 실행 성능은 구분**한다. 아래 Unity 설계는 원리를 채택한 신규 설계이며 원본 소스 복원·엔진 간 직접 이식이 아니다.

## 현재 병목과 이미 적용한 부분

[1차 결과](D:/Dev/ClouDream/Docs/CloudOptimization-2026-10-01.md)의 RTX 4080 Editor 계측에서 ON 75% Raymarch p50은 home 37.77ms, side 58.16ms, 내부 후보 7.26ms였다. 전체 프레임 시간이 아니며 GTX 1660 성능으로 환산할 수 없다. 추가 후보의 향상률도 아직 측정하지 않았다.

[CloudRaymarch.compute](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute:298)와 [CloudConceptShapes.hlsl](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudConceptShapes.hlsl:530)의 현재 비용 구조:

- 시선 광선은 넓은 고도 구간을 최대 42km까지 평가한다. Concept V2 루프 상한은 1,024회이며 실제 실행 횟수는 시점마다 다르다.
- 빈 구간에도 밀도 함수를 호출한다. 상층 지지 영역 밖의 몸체 계산은 이미 생략하지만, 광선이 다음 유효 구간으로 이동하는 가속은 없다.
- 구름 진입 시 이분 탐색 5회와 밀도 재평가, 세부 법선용 밀도 6회, 큰 법선용 필드 6회 및 형태 선택을 수행한다.
- 조명은 이미 광선 내부 60m 간격에서 재사용한다. 새 조명 기준점마다 태양 방향 밀도 최대 7회, 하늘 조명 사용 시 위쪽 밀도 3회를 계산한다. 태양 루프는 현재 7회를 모두 돈다.
- 운해·상층·띠의 큰 형태를 먼저 구하고, 충분히 멀면 굴곡 노이즈를 읽기 전에 반환하는 검사도 이미 있다.

상층 셀의 불변 파라미터 캐시, 지원 영역 밀도 조기 거부, 작은 출력 RT 형식, 렌더 해상도 조절, 시선 투과율 조기 종료, 조명용 세부 형태 생략은 **기존 구현**이다. FluidNinja에서 같은 원리를 찾았다고 추가 이득으로 계산하지 않는다.

## 우선 검증할 후보

| 순서 | 후보 | 실제 자료의 근거 | ClouDream에서 줄일 비용 | 판단 |
|---|---|---|---|---|
| 1 | 보수적인 경계로 빈 광선 구간 건너뛰기 | MF_RayBox의 진입·이탈·두께 출력, SVOL의 MaxT 종료와 높이 범위 검사 | 빈 하늘에서 반복하는 전체 밀도 평가 | 큰 잠재력. 기존 화풍을 유지하는 첫 실험으로 적합 |
| 2 | 그림자 광선의 경계·기여도 조기 종료 | SVOL DirLit/DirLitFlow의 경계 이탈 및 lightvisibility 임계값 종료 | 기준점당 7회인 태양 방향 밀도 평가 일부 | 중간 잠재력. 작은 변경으로 독립 비교 가능 |
| 3 | 운해의 높이·봉우리 마스크를 별도 2D 필드로 캐시 | Field/Painter/Sim 분리, 낮은 입력 해상도, SVOL의 HeightMap 조회 | 운해 밀도와 법선에서 반복하는 높이 함수 | 운해 시점에서 유망. 보간 오차와 경계 연속성 검증 필요 |
| 4 | 시선·조명·미세 굴곡의 품질을 독립 조절 | MaxViewSteps, Noisy/Noiseless·Lit/Unlit 선택 경로 | 내부 적분, 조명 갱신, 고주파 계산 | 큰 절감 가능성, 표현 손실을 동반하는 선택지 |
| 5 | Concept V2에 필요한 셰이더 경로만 특수화 | StaticSwitch에 연결된 별도 Custom HLSL | 불필요한 중간 계산·코드 규모, 경우에 따라 레지스터 부담 | 영향 미정. 대규모 효과를 약속하지 않는 보조 후보 |

이 순서는 GPU 실측으로 확정한 병목별 순위가 아니다. 현재 코드의 반복 비용과 화풍 보존 가능성으로 정한 실험 순서다. 후보 간 절감은 겹치므로 개선율을 단순 합산하면 안 된다.

### 1. 빈 공간에서 밀도 함수를 부르지 않기

**실제 근거:** [MF_RayBox JSON](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/evidence/readable/OutputMaterials/Base/Functions/SVOL/MF_RayBox.json)의 export 23/25가 tN/tF, 15/16/17이 BoxThickness/RayExit/RayEntry다. 이 계산은 그래프에 분산되어 있다. 추출된 Custom #9만으로 완성된 ray-box 교차 코드라고 설명하면 부정확하다. [DirLitFlow #54](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/evidence/embedded_code/OutputMaterials/Base/M_NinjaOutput_BaseMaterial_SMOKEVOLUME/00054_MaterialExpressionCustom_5.hlsl)는 MaxT를 넘으면 시선 루프를 종료하고, 높이 범위 안에서만 밀도·노이즈를 계산한다.

**Unity 제안:** 기존 상층 셀 창에 보수적인 지지 영역 정보를 추가하고, 광선과 셀·경계의 교차 구간에서만 밀도를 평가한다. 운해, 상층, 띠의 유효 구간을 함께 고려한다. 한 구름을 벗어났다는 이유로 뒤쪽의 다른 구름까지 종료하면 안 된다. 첫 단계는 빈 상층 셀과 상층 타원체 경계로 한정하고, 운해와 띠는 기존 평가를 유지할 수 있다.

현재 상층 최종 밀도는 회전 타원체 경계로 제한되므로 이를 이용할 근거가 있다. 반면 운해는 연속 높이장이고 띠는 굴곡·smooth union·변위가 있으므로 별도의 안전한 범위가 필요하다. 현재 `ConceptEllipsoidField`는 정확한 거리장이 아니다. 그 값을 그대로 이동 거리로 사용하는 sphere tracing은 채택하지 않는다. 보간한 낮은 해상도 밀도에서 값이 0이라는 사실도 안전한 빈 구간 증명이 아니다.

형태의 정의를 바꾸지 않아도 샘플 시작 위치가 달라지면 적분 결과가 달라질 수 있다. 구간 경계에서 진입 탐색을 복구하고, 최대 변위·얇은 띠·셀 경계·원점 이동·불투명 물체 가림을 검사해야 한다. 근경 내부처럼 빈 구간이 짧은 장면에는 이득이 작을 수 있다.

### 2. 그림자가 이미 확정된 뒤의 조회 생략

**실제 근거:** [DirLit #53](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/evidence/embedded_code/OutputMaterials/Base/M_NinjaOutput_BaseMaterial_SMOKEVOLUME/00053_MaterialExpressionCustom_4.hlsl)과 #54의 그림자 루프는 최대 20회지만, 볼륨 범위를 벗어나거나 `lightvisibility < .001`이면 종료한다. ClouDream의 `EvaluateCloudLight`에는 이에 대응하는 누적 차폐 종료가 없다.

**Unity 제안:** 현재 명암식에서 남은 태양광 기여가 충분히 작아졌음을 판단해 태양 방향 밀도 조회를 끝낸다. 또는 남은 모든 샘플이 전체 구름 지원 영역 밖에 있음을 증명할 때 종료한다. 하늘 주변광 세 샘플은 태양 차폐와 별개이므로 그대로 유지한다.

FluidNinja의 .001을 그대로 적용하면 안 된다. ClouDream에는 `exp(-tau * .72)` 직접광 외에 `exp(-tau * .09)` 다중 산란 근사, 별도 팔레트 명암과 rim이 있다. 빨리 감쇠하는 항 하나만 보고 중단하면 느리게 감쇠하는 항의 오차가 커질 수 있다. 현재 시간대 프리셋의 팔레트 혼합은 1이지만 일반 혼합 경로도 지원한다. 활성 명암식과 광원 강도를 반영한 오차 기준을 설계해야 한다.

이 기법은 새 3D 버퍼 없이 시험할 수 있다. 원래 7회뿐이므로 FluidNinja의 최대 20회에서 얻을 수 있는 절감과 같다고 보지 않는다. 짙은 구름, 역광, 낮은 태양 고도, 서로 떨어진 구름 사이 차폐를 비교한다.

### 3. 운해 높이장의 계산과 조회 분리

**실제 근거:** [심층 분석 02](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/02_GPU유체로직_심층분석.md), [비용 분석 03](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/03_최적화_비용과대가.md), NinjaLiveComponent 기본값은 FieldBufferDownScaleFactor=4, PaintBufferDownScaleFactor=2다. 단계를 서로 다른 격자로 계산하고 출력 머티리얼은 HeightMap을 읽는다. 이 값은 입력 필드 해상도이며 화면 렌더 해상도와 다르다. SimplePainter처럼 최종 출력으로 직접 쓰는 경로에는 예외도 있다.

**Unity 제안:** [ConceptOceanHeight](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudConceptShapes.hlsl:91)의 높이·peakMask만 월드 XZ의 2D 필드로 준비한다. 현재 함수는 호출마다 3D 노이즈 두 번, 방향 회전, 여러 remap을 수행하며 밀도·법선·조명에서 반복된다. 상층 셀 캐시는 이 운해 계산을 줄이지 않는다.

우선 RG32F로 수치 비교하고, 허용 오차를 정한 뒤 정규화한 RG16F도 비교한다. 단일 256² RG16F는 0.25MiB이지만 이것만으로 해상도가 충분하다고 결론 내릴 수 없다. 큰 월드 범위에서는 texel당 수십~수백 미터가 될 수 있다. 월드 texel 크기·범위를 따로 정하고 창 외부는 기존 함수를 사용한다.

카메라 주변 창을 정수 texel 단위로 이동시키고 새로 노출된 부분만 갱신하는 방식은 **ClouDream용 확장 설계**다. FluidNinja의 이동 sim 좌표 보정에서 착안할 수 있지만 해당 캐시가 그대로 구현되어 있다는 뜻은 아니다. 현재 높이 함수는 바람 시간에 의존하지 않으므로 카메라 창·원점·형태 파라미터 변경을 갱신 원인으로 삼을 수 있다. 향후 프로필이 시간을 사용하면 그 계약도 바뀐다.

낮은 해상도 선형 보간은 원래 함수를 정확히 재현하지 않는다. 봉우리 마스크와 큰 법선에 오차가 전파된다. **셀 중심 높이 하나로 운해 전체를 대체하지 않으며**, 캐시와 절차식 경계 왕복·원점 이동·날씨 변경 도중 재전환을 검사한다. 너무 잦은 전체 재생성으로 이득을 상쇄하지 않는지도 포함한다.

### 4. 품질을 한 슬라이더로 함께 낮추지 않기

**실제 근거:** [기본 파라미터 표](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/evidence/material_parameter_defaults.csv)의 SVOL MaxViewSteps 기본값은 100이다. [MI override 표](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/evidence/material_instance_overrides.csv)에는 80, 100, 140, 160, 200 등도 있다. 하나의 고정 수치가 모든 출력의 실제 스텝 수를 나타내지 않는다.

Unity에서는 내부 적분 간격, 조명 기준점 간격, 그림자 샘플 품질, 먼 곳의 미세 굴곡을 독립적으로 비교할 가치가 있다. 현재 1,024는 루프 상한이고 FluidNinja의 100은 정규화된 단일 볼륨의 StepSize에도 연결된다. 단순히 상한을 100으로 낮추면 먼 구름까지 도달하지 못할 수 있으며, 10배 개선으로 해석할 수 없다.

거리 또는 화면상 크기를 기준으로 디테일을 완만하게 줄이는 것은 Unity용 신규 설계다. 가까운 윤곽·구름 관통·얇은 띠는 유지하고 먼 내부를 먼저 낮추는 편이 목적에 맞다. 노이즈를 끄거나 Unlit으로 바꾸는 것은 형태·명암 변경이므로 기존 화풍을 유지하는 기본 최적화에 자동 포함하지 않는다.

### 5. 기능별 셰이더 경로 분리

**실제 근거:** [SMOKEVOLUME JSON](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/evidence/readable/OutputMaterials/Base/M_NinjaOutput_BaseMaterial_SMOKEVOLUME.json)에서 #398은 #49/#50, #399는 #51/#52, #400은 #54/#53을 선택한다. #395~397의 이름은 UsePerformanceHeavyNoise다. #401 Lit, #402 LightSourceIsPointLight가 위 경로를 선택한다. 저장된 여섯 HLSL을 매 픽셀마다 모두 실행하는 구조라고 볼 근거가 없다.

Unity에서는 현재 Concept V2 + 팔레트 전용 경로를 compute kernel 또는 제한된 keyword 조합으로 분리할 수 있다. 현재 `CloudIncidentLight`는 physicalLight를 만든 뒤 conceptLight와 혼합하며, 시간대 프리셋은 cloudPaletteBlend=1이다. 완전 팔레트 상태에서 불필요한 physical 항을 생략하는 후보가 된다. 중간 혼합값은 기존 경로로 처리해야 한다.

단, 균일 분기는 컴파일러가 이미 효율적으로 처리할 수 있다. 코드 줄 수 감소가 GPU 시간 감소는 아니다. 기존 스타일을 제거하지 않고 compiled 경로의 GPU A/B로 판정한다. 품질 토글 조합을 무제한 늘리면 variant·전환 비용이 생기므로 필요한 조합만 둔다.

## 엔진 기능과 별도로 설계해야 하는 후보

**공간 조명 캐시:** Unreal Heterogeneous Volume의 내부 lighting cache와 Lighting Downsample Factor는 엔진 렌더러 기능이다. SVOL #53/#54는 직접 그림자 루프를 수행한다. 따라서 “FluidNinja가 모든 출력에서 자체 조명 캐시를 사용한다”는 근거로 인용하면 안 된다. [Epic UE 5.6 문서](https://dev.epicgames.com/documentation/en-us/unreal-engine/heterogeneous-volumes-in-unreal-engine?application_version=5.6)는 시선·그림자 스텝 및 조명 캐시 해상도를 별도로 설명한다.

ClouDream에서는 기존 광선별 60m 보간을 넘어, 여러 픽셀이 공유하는 태양 광학 깊이·하늘 차폐장을 낮은 해상도 3D 타일로 계산하는 설계가 가능하다. 다층 구름에서는 높이가 다른 점의 차폐가 다르므로 단일 XZ 텍스처 하나로 치환하면 안 된다. 태양·날씨·형태·바람·원점 변경에 맞는 갱신과 밖의 구름이 드리우는 그림자 범위가 필요하다. 큰 잠재력이 있지만 개발·검증 범위가 커서 앞선 후보의 계측 후 결정한다.

**시간 지터와 재투영:** SVOL 코드에는 화면 좌표와 StateFrameIndexMod8로 시작점을 흔드는 처리가 있다. 그 HLSL 안에서 과거 프레임을 재투영하는 로직은 확인하지 못했다. 지터만 넣으면 샘플 수가 줄지 않으며, 저샘플과 함께 쓰면 화면이 흔들릴 수 있다. Unity HDRP 자체 클라우드는 별도 temporal accumulation과 ghosting 제어를 갖지만, 현재 CustomPass에 자동으로 같은 이력이 제공되는 것은 아니다. 설치된 [HDRP 클라우드 문서](D:/Dev/ClouDream/Library/PackageCache/com.unity.render-pipelines.high-definition@dd309ef59526/Documentation~/volumetric-clouds-volume-override-reference.md)를 대조했다. 기존 계획의 깊이 인식 재구성·시간 재투영 후보는 여전히 유효하되 이번 자료에서 새로 발견한 완성 구현이라고 표현하지 않는다.

**업데이트 주기와 가시성:** NinjaLiveComponent에는 PauseSimWhenNotVisible, 거리별 sampling FPS, ParamUpdateFrequency가 있다. 이는 동적 상태 계산의 생략 원리다. 전체 화면 구름 Raymarch를 30Hz로만 실행하면 비행 중 시점이 어긋나므로 그대로 옮기지 않는다. 우선 캐시 갱신과 향후 국소 유체에 적용할 원리로 남긴다. 화면 밖 구름도 화면 안 구름에 그림자를 만들 수 있으므로 가시성만으로 차폐 데이터를 제거하지 않는다.

## 이번 최적화에서 제외할 항목

| 항목 | 제외 이유 |
|---|---|
| 2D 유체 상태, 두 단계 공간 해싱, 분리형 압력 커널 | 현재 렌더러에 해당 유체·다수 자극 처리 비용이 없다. 지금 추가하면 새 기능과 비용이 생긴다. 향후 후류 단계에서 검토 |
| 세계 전체를 하나의 2D 밀도 extrusion으로 교체 | 상층 덩어리, 여러 고도, 관통 형태가 바뀐다. 현재 연속 운해 및 화풍 보존 요구와 맞지 않음 |
| 두 위상 FlowMap3d | 움직이는 디테일 표현이며 3D 텍스처를 두 번 읽는다. 현재 렌더 비용 절감으로 볼 수 없음 |
| 16bit RT 채택 자체 | 이미 관련 출력 버퍼 최적화 적용. 추가 채널 삭제는 읽는 곳을 확인한 보조 정리이며 큰 병목 해결책으로 보지 않음 |
| “Lookup이 25% 빠르다” | [Pressure 연결 추적](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/evidence/pressure_graph_trace.txt)과 02·06장의 분석상 Lookup #83은 최종 출력 연결이 끊김. 활성 #82의 실측 개선율로 사용 불가 |
| 매뉴얼 데모 FPS를 목표 하드웨어 성능으로 사용 | 엔진·장면·GPU·설정 묶음이 다르며 개별 기법의 기여를 분리하지 못함 |

압력 루프의 배열 범위, 발산 코드의 대입 조건문, 움직임 길이 0 처리 등 [자료 06장의 미검증 문제](C:/Users/pgu51/Downloads/분석/FluidNinjaLive_2.0.1.56_실제구현분석/06_검증범위와주의점.md)도 그대로 복사하지 않는다. 이는 현재 최적화 대상이 아닌 후속 유체 구현 시 참고 사항이다.

## 다음 구현 제안과 판정 기준

먼저 **빈 구간 제한과 그림자 종료**를 독립적으로 구현·비교하고, 그다음 **운해 높이장 캐시**의 정밀도별 결과를 비교하는 순서를 제안한다. 셰이더 특수화는 독립 실험으로 작게 추가할 수 있다. 명확한 성능 차이가 확인되면 기존 F8 설정창에 해당 토글과 품질 단계를 넣는다. 추측만으로 모든 항목을 사용자 설정으로 노출하지 않는다.

깊이 인식 업샘플링, 시간 재투영, 공간 조명 캐시, 거리별 품질 저하는 표현 절충이 더 크므로 결과를 보고 별도 선택한다. 후류·유체는 기존 결정대로 후속 범위다.

검증은 Unity MCP로 ClouDream 인스턴스를 고정한 뒤 수행한다. 기존 OFF/ON 시점에 하늘 위주·수평선·급회전·전경·구름 내부·낮은 태양을 추가하고, 각 후보 단독과 조합을 비교한다. `Cloud.Raymarch` 외에 캐시 생성·갱신·합성 비용과 전체 GPU 프레임 p50/p95도 포함한다. 화면 크기·프로필·카메라·조명은 동일하게 고정한다.

빈 구간 최적화는 상층 분포와 GPU 안정성, 높이 캐시는 운해 연속성·창 경계·원점 이동, 환경 의존 캐시는 날씨 전환 중단/재전환을 검사한다. 형식별 밀도·RGB·투과율·대표 깊이 오차와 이동 영상의 깜빡임·빛샘을 함께 판단한다. 1차 계측은 현재 장비에서 후보 선택용이고, GTX 1660 / FHD / 60fps 달성은 해당 GPU의 Player 전체 프레임 측정으로 별도 확인해야 한다.
