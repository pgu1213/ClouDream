from pathlib import Path
p=Path('Assets/LostSkiesClouds/README.md')
s=p.read_text(encoding='utf-8-sig')
s=s.replace('## 실행', '''## 2026-09-21 상층 구름 확장

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

## 실행''',1)
s=s.replace('2 구름 위', '2 상층 구름 위(9,000m)')
s=s.replace('**Tower Coverage / Cloud Density**: 상층 구름의 양과 두께.', '**Tower Coverage / Cloud Density**: 기존 저층 타워의 분포와 전체 광학 밀도. 새 상층 형태는 SparseSky 에셋에서 조절한다.')
s=s.replace('**Brightness / Shadow Tint / Highlight Tint**: 표면 명암과 색.', '**Brightness / Shadow Tint / Highlight Tint**: 환경 공급자가 없을 때의 기본값. 현재 장면은 연결된 Weather-Clear 에셋의 색을 사용한다.')
s=s.replace('추가 운해를 끈다.', '추가 운해와 상층 구름을 끈다.')
s=s.replace('추출된 `Expanse.dll`의 주요 렌더링/생성 함수는 IL2CPP 더미 본문이다.', '초기 추출된 `Expanse.dll`의 주요 함수는 IL2CPP 더미 본문이었다. 2026-09-19 보강 자료에는 네이티브 ISIL과 상태 전환 등의 수작업 복원본이 추가되었지만 원본 HLSL 소스는 제공되지 않는다.')
s=s.replace('herald는 참고용 보존 자료이며, 프리셋 간 완전한 런타임 전환은 아직 제공하지 않는다.', 'herald는 참고용 보존 자료이며 원본 normal/herald의 모든 필드를 혼합하는 전환은 제공하지 않는다. 새 CloudWeatherProfile은 현재 연결된 밀도·coverage 변화량·색상을 런타임에 전환한다.')
s=s.replace('최종 캡처: 프로젝트 루트 `Screenshots/LostSkies-CloudSea.png`.', '이전 운해 캡처: `Screenshots/LostSkies-CloudSea.png`. 최신 상층 포함 캡처: `Screenshots/SkyClouds-Final.png`.')
p.write_text(s,encoding='utf-8')
