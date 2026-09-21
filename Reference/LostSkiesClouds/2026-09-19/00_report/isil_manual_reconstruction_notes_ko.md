# ISIL 수작업 복원 노트

## SkyChangeController

신뢰도: **HIGH**, 단 `Update`의 원래 소스 형태는 **MEDIUM**.

- `Awake`/`OnDestroy`는 SkyBossService의 전투 진입·이탈 이벤트와 WindwallService의 로컬 플레이어 진입·이탈 이벤트를 대칭적으로 구독·해제한다.
- `Start`는 글로벌 라이팅 Volume에서 `ColorAdjustments`를 가져오고, coverage 곡선 키 개수와 노출 곡선 키 개수를 검사한다.
- coverage 검사는 현재 ProceduralCloudVolume, Default, Herald 세 곡선을 비교한다. Windwall 곡선은 이 검사에 포함되지 않는다.
- 노출 검사는 Default와 Herald만 비교한다.
- `SetSkyProfile`은 전환 도중 다시 호출되어도 끊김을 줄이기 위해 현재 실시간 값을 `_tempProfile`에 스냅샷한다.
- `LerpTime == 0`이면 `_activeProfileType`을 갱신하거나 새 작업을 시작하기 전에 반환한다.
- 기존 CancellationTokenSource가 있고 아직 취소되지 않은 경우에만 Cancel과 Dispose를 호출한다.
- 상태 머신은 매 프레임 `Clamp01(time / LerpTime)`으로 coverage, raininess, boss-fight 값을 보간한다.
- `UpdateBossFightValue`는 한 줄짜리 메서드이므로 네이티브 상태 머신에서는 필드 쓰기로 인라인된 것으로 판단했다.
- `Update`와 `UpdatePostExposure`는 동일한 네이티브 본문으로 병합되어 있다. 복원본은 읽기 쉬운 `Update -> UpdatePostExposure` 형태를 택했다.
- 노출 곡선은 같은 프레임에 두 번 Evaluate된다. 한 번은 `postExposure.value`, 한 번은 디버그용 `_exposureCurveValue`에 사용된다.
- 곡선 혼합은 time, value, inTangent, outTangent를 보간하고 weightedMode는 A에서 복사한다. inWeight/outWeight는 관찰된 본문에서 별도로 쓰지 않는다.

## CloudLayerInterpolator

신뢰도: **HIGH**.

- 기본값은 auto mode=true, transition time=10초, bypass offset=true다.
- 매 Update마다 세 배열을 6개 레이어 순서로 채운다: Coverage, Base, Structure, Detail, BaseWarp, DetailWarp.
- 대상이 없으면 interpolation amount를 0으로 만든다.
- 대상은 있지만 현재 프리셋이 없으면 대상을 즉시 현재 프리셋으로 승격한다.
- 자동 보간 증가량은 `Min(1, deltaTime / transitionTime)`이다.
- 프리셋이 바뀌면 모든 TextureInterpolator에 `TexturesChanged`와 `ForceUpdate`를 호출하고 대응 ProceduralNoiseGenerator를 재설정한다.
- Coverage(index 0)는 항상 2D이며 나머지는 프리셋 geometryType으로 차원을 계산한다.
- generator에 복사되는 값은 noiseType, floor(scale), octaves, octaveScale, octaveMultiplier다.
- 현재와 대상이 모두 있으면 UniversalCloudLayer의 연속 설정을 먼저 보간하고 BaseCloudLayer에 적용한다.
- 각 TextureInterpolator에는 current/target generator, static texture, static 여부, blend 값이 설정된다.
- coverage 정적 텍스처가 없고 procedural도 아니면 검은 텍스처를 대체값으로 사용한다.
- 첫 전환 프레임이 지난 뒤부터 보간 텍스처를 BaseCloudLayer.SetTexture로 밀어 넣는다. 타일 값은 target noise layer의 tile을 사용한다.

## UniversalCloudLayer.lerp

신뢰도: 구조 **HIGH**, 모든 필드의 소스 표기 **MEDIUM**.

- 4인자 오버로드는 전달된 결과 객체를 재사용한다.
- 연속 float와 Vector2 값은 clamp된 선형 보간을 사용한다.
- enum/bool/참조형처럼 연속 보간할 수 없는 값은 대체로 `x < 0.5`에서 A, 그 외 B를 선택한다.
- noiseLayers는 선택한 쪽의 구조체를 복사한 뒤 tile 값만 A/B 사이에서 보간해 정수화한다.
- densityCurve와 coverageCurve 배열은 요소별로 보간한다.
- 두 배열에서 `Utilities.curveFromSamples`를 호출해 densityAnimationCurve와 coverageAnimationCurve를 다시 만든다.
- customPostProcessPasses는 0.5 임계값으로 A 또는 B 목록을 선택한다.
- `UniversalCloudNoiseLayer.lerp(a,b,x)` 자체는 관찰된 네이티브 본문에서 A 구조체를 그대로 복사하며 b와 x를 사용하지 않는다. 실제 텍스처 혼합은 CloudLayerInterpolator가 별도로 담당한다.

## CloudRemapping

신뢰도: **HIGH**.

- GlobalSettings 참조가 없으면 `FindAnyObjectByType<GlobalSettings>`로 찾는다.
- floating-origin 이동 벡터를 GlobalSettings의 planet origin과 로드된 CloudManager의 planet origin에서 뺀다.
- 원점 이동 이벤트 해제 메서드는 해당 타입의 ISIL에서 발견되지 않았다.

## SkyParticlesController

신뢰도: 계산과 VFX 값 **HIGH**, 원래 메서드 인라인 경계 **MEDIUM**.

- 섬 안/밖 상태에 따라 기본 mote spawn rate와 VFX bool을 갱신한다. 전투 중에는 즉시 VFX를 바꾸지 않는다.
- Windwall 거리를 AnimationCurve로 평가해 debris intensity에 전달한다.
- 보스전 debris intensity는 `Clamp01(1 - CurrentValue / CurrentMaxValue)`다.
- 보스 위치를 매 프레임 arena debris position으로 전달한다.
- 보스 체력 변경 이벤트는 전투 진입 시 구독하고 이탈 시 해제한다.

## TimeOfDayController의 구름 관련 부분

신뢰도: **HIGH**.

- TimeOfDay와 BossFightValue는 단순 필드 접근자다.
- remapping이 활성화되면 `_remapCurve.Evaluate(_timeOfDayValue)`를 사용한다.
- gameplay density query 결과를 low/high density 사이에서 0~1로 정규화한다.
- 프레임 보간 계수는 `Clamp01(deltaTime * cloudDensitySmoothFactor)`다.
- 전체 조명·태양·달·안개 Update는 규모가 크고 구름 외 로직이 많이 섞여 있어 이번 소스 형태 복원에서 제외했지만 원시 ISIL은 그대로 보존했다.
