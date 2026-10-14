# 동일 baseline 4회 연속 실행

ReleaseBaselineStability: 2026-10-05 독립 Player, baseline만 4회, 각 540프레임. 기존 ABBA와 섞지 않은 원인 분리 실험이다.

| 회차 | wall 평균 ms | Frame GPU 평균 ms | FPS | shapeBuild 시작→끝 | GPU MHz / °C / W 평균 |
|---|---:|---:|---:|---|---|
| 1 | 42.601620 | 42.635304 | 23.473286 | 14→26 | 2744.35 / 62.35 / 190.67 |
| 2 | 43.304270 | 43.281283 | 23.092411 | 41→53 | 2744.35 / 63.91 / 190.06 |
| 3 | 43.024188 | 43.002336 | 23.242740 | 68→80 | 2745.00 / 63.74 / 191.18 |
| 4 | 43.088339 | 43.069645 | 23.208135 | 95→107 | 2744.35 / 63.96 / 191.86 |

마지막/첫 wall 변화 1.142488%, 네 회 반복 범위/평균 1.633895%.

네 실행의 실제 포즈, passSettings, 환경/프로필, 빌드, FHD 및 내부 구름1440×816 해상도, 고정 시각은 동일하다. renderer를 유지하는 동일 shader 연속 실행임은 shapeBuild 누적 카운터가 계속 증가하는 것으로도 확인된다.

약43ms가 네 번 유지되어, ABBA에서 마지막 baseline이 약28ms로 내려간 현상을 단순한 프로세스 경과 시간이나 동일 경로 반복만으로 설명하지 못한다. 다음 분리 대상은 해상도/거리 키워드 변경, current shader 사용, renderer 교체, 이들이 유발하는 GPU 공통 상태다. 어느 하나가 원인이라고 확정한 것은 아니다.

다음 실험으로 baseline→reference-performance→baseline을 사용하면 같은 보존 shader/renderer 안에서 해상도와 거리 키워드만 바뀐 영향을 분리할 수 있다. 성능 좋은 반복만 골라 기존 결과를 대체하지 않는다.

## 후속 ReferenceSwitch 결과

[Benchmark-Reference-Switch.md](Benchmark-Reference-Switch.md)에서 독립 프로세스 첫 baseline28.61ms, reference-performance21.21ms, 마지막 baseline32.54ms를 관측했다. 따라서 측정 case 사이의 current shader 교체를 편차 원인으로 확정할 수 없다. 또한 모든 suite는 Configure 전45프레임에 원래 scene shader를 렌더할 수 있으므로, baseline-only가 프로세스 전체에서 current shader를 실행하지 않았다고 가정하면 안 된다.
