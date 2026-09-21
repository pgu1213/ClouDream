# ClouDream — Cloud Sea / 1차 볼류메트릭 프로토타입

## 실행

`Assets/CloudSea/Scenes/CloudSea.unity`를 열고 Play를 누릅니다. 메뉴 `ClouDream > Open Cloud Sea`에서도 열 수 있습니다. Game 창을 클릭한 뒤 조작하세요.

| 입력 | 동작 |
|---|---|
| WASD | 카메라 기준 이동 |
| Q / E | 월드 기준 하강 / 상승 |
| 우클릭 누른 채 마우스 | 시선 회전 |
| Shift | 4배 가속 |
| 휠 | 비행 속도 조정 |
| 1 / 2 / 3 | 기본 비행 시점 / 상공 / 구름 내부 |
| R | 시작 위치·시선 복귀 |
| H | 안내 표시 전환 |

## 구현

Unity 6000.6.2f1, HDRP 17.7.0의 **Volumetric Clouds / Manual** 모드에 자체 생성한 날씨 맵과 고도 프로필을 연결합니다. 원본 OutdoorsScene은 보존하고 별도 씬을 생성했습니다. 프로젝트 기본 렌더 파이프라인과 현재 품질 단계는 볼류메트릭 구름 지원을 켠 `CloudSeaHDRP.asset`을 사용합니다.

- 고도 650~3,850m의 구름 볼륨. 카메라 시작 고도 2,400m.
- 512² 날씨 맵: R=운량, G=강우 감쇠(현재 0), B=구름 높이 유형, A=최대 고도(1).
- 128² 선형 Half 정밀도 LUT: X=구름 유형, Y=정규화된 고도. R=밀도 프로필, G=형태·침식 가중치, B=환경광 투과 가중치.
- 하부의 모든 유형에 밀도 코어를 유지해 구름 바다가 아래까지 뚫리는 현상을 억제합니다. 상부에는 서로 다른 높이와 3D 노이즈 침식을 적용합니다.
- 날씨 맵은 시드 기반으로 생성되며 16km마다 반복됩니다. 양쪽 경계 값을 일치시켰습니다.
- HDRP가 3D 형태/침식 노이즈, 레이마칭, 태양광 자체 그림자, 다중 산란 근사, 깊이 합성, 시간 누적을 담당합니다. 기본 128 primary / 8 light steps.
- 풍속 18km/h, 방향 25도. 형태·침식에 서로 다른 이동 배율과 수직 변화를 사용합니다.
- 런타임에서는 Volume 프로필을 복제하므로 Play 중 조정이 저장된 프로필을 덮어쓰지 않습니다.

## 조정

Hierarchy의 `Cloud Sea - Weather and Height Profiles`를 선택합니다.

1. `Density`, `Shaping`, `Shape Scale`, `Erosion`, 품질 스텝, 바람은 즉시 적용됩니다.
2. 높이 유형, `Ocean Depth`, `Coverage`, `Tower Amount`, `Seed`를 바꾼 뒤 **Bake and save maps**를 누르고 씬을 저장합니다. Play 중 같은 버튼은 임시 미리보기만 생성합니다.
3. `Shape Scale`이 커지면 덩어리가 작아집니다. `Shaping`과 `Erosion`을 동시에 과도하게 올리면 상부 구름이 사라질 수 있습니다.
4. `Primary Steps`를 192~256으로 높여 근거리 품질을 비교할 수 있습니다. GPU 비용도 증가합니다.
5. 카메라의 Field of View, Sun 회전, `CloudSeaDay`의 고정 노출을 함께 조정할 수 있습니다.

컨트롤러가 관리하는 Volumetric Clouds 필드는 컨트롤러에서 수정하세요. 프로필만 직접 바꾸면 다음 Apply 때 컨트롤러 값이 다시 적용됩니다.

## 참조 분석 반영 범위

사용자 제공 `Lost_Skies_Cloud_System_Analysis.md`의 **넓은 구름층 + 고도 밀도 구배 + 형태/침식 분리 + 자체 그림자** 구조를 적용했습니다. 첨부 컨셉의 빈틈 적은 운해와 수직 구름을 1차 방향으로 삼았습니다. 자료의 Expanse 전용 수치와 추출 바이너리는 HDRP에 그대로 이식하지 않았습니다. 생성한 맵은 자체 절차적 데이터이며 Expanse DLL이나 Lost Skies 에셋에 의존하지 않습니다.

현재는 **볼류메트릭 생성 기반**입니다. 컨셉 아트의 청록색 그림자·크림색 가장자리, 회화적인 표면, 구름별 조형 제어, 노을/폭풍 전환은 후속 작업입니다. 6단계 Expanse GPU 베이커, 독립된 로컬 볼륨, GPU 밀도 질의, 충돌·걷기 가능한 표면도 이번 구현에는 포함하지 않습니다. 구름은 비행하며 통과하는 렌더링 볼륨입니다.

큰 화면에서 근거리 노이즈/부드러운 재구성 흔적이 남을 수 있습니다. 이 단계는 최종 스타일과 성능 목표가 확정되기 전의 프로토타입입니다. 다른 Quality 단계로 바꿀 경우 그 단계의 HDRP 에셋에서도 Volumetric Clouds 지원을 켜야 합니다.

공식 API/기능 참고: [Unity Volumetric Clouds Volume Override](https://docs.unity.cn/Packages/com.unity.render-pipelines.high-definition%4017.6/manual/volumetric-clouds-volume-override-reference.html). 실제 구현 API와 밀도 연산은 설치된 HDRP 17.7 소스로 확인했습니다.

## 재생성 및 검증 (Unity CLI)

프로젝트 루트에서 실행합니다. PATH에 없다면 `%LOCALAPPDATA%/Unity/bin/unity.exe`를 사용합니다.

```powershell
unity command run_script --file AgentScripts/BuildCloudSea.cs --entry BuildCloudSea.Build --json
unity command run_script --file AgentScripts/ValidateCloudSea.cs --entry ValidateCloudSea.Validate --json
unity command editor_play --json
unity command run_script --file AgentScripts/ValidateCloudSea.cs --entry ValidateCloudSea.FlightTest --json
unity command editor_stop --json
unity command run_script --file AgentScripts/InspectCloudSea.cs --entry InspectCloudSea.Capture --args '["CloudSea-Day",0]' --json
```

`Build`는 생성한 CloudSea 씬·프로필·맵을 기본값으로 재구성합니다. 직접 편집한 결과가 있다면 먼저 별도로 저장하세요. 현재 씬에 미저장 변경이 있으면 실행을 중단합니다. 일반 조정에는 재생성 대신 Inspector를 사용하세요.

확인한 항목:

- 최종 C# 컴파일 성공.
- 같은 시드의 맵 일치 / 다른 시드의 변화.
- 날씨 맵 양쪽 경계 차이 0, 최소 운량 0.9333.
- 모든 유형의 하부 코어 연속성 및 위·아래 경계의 밀도 감쇠.
- 구름 렌더 지원, 카메라·태양·씬 스크립트·에셋 참조.
- Play 모드 입력으로 약 108m 전진 후 R 키 시작 위치 복귀.
- 기본·상공·내부 시점의 실제 HDRP 카메라 캡처. `Screenshots/`에 저장.

입력 검증은 임시 가상 키보드와 테스트 동안의 Game 입력 라우팅을 사용하며 종료 시 복구합니다. 에디터에서 GPU frame time이 0으로 보고되어 신뢰할 수 있는 GPU 성능 수치는 확보하지 못했습니다. 빌드 성능 벤치마크는 아직 수행하지 않았습니다.
