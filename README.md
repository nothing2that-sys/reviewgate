# ReviewGate

**Headless CLI를 중심으로 원문 근거를 추적하는 범용 AI 검수 도구.**

OpenAI, Together AI, Google AI의 제공자와 모델을 선택하여 소설 원고, 코드, 기획서·설계서 등을 검수하고, 필요할 때 다른 모델로 독립 검수하거나 특정 지적을 추가 검증하는 것을 목표로 합니다.

기본 사용 경험은 Codex CLI를 Headless로 호출하는 방식과 유사하게 설계합니다. 검수 목적·입력·모델·옵션을 전달하면 GUI 없이 실행하고, 사람이 읽는 보고서와 기계가 처리할 수 있는 결과·종료 상태를 반환합니다. 특정 Codex CLI 명령이나 동작의 완전한 복제를 뜻하지는 않습니다.

## 현재 상태

- 프로젝트명: **ReviewGate**
- 저장소: [nothing2that-sys/reviewgate](https://github.com/nothing2that-sys/reviewgate)
- 단계: **v0.1 오프라인 설계 기준본 완료 — 독립 검토·지적 반영 완료**
- 구현 코드·실행 가능한 CLI·빌드·테스트·설치 절차: **아직 없음**
- 제품 모델 API 호출·API 키 발급/입력/저장/연결: **하지 않음**. 별도로 사용자 요청의 기존 Claude Code Headless 독립 설계 검토를 수행했습니다.

이 저장소의 문서는 합의 사항과 검토 중인 제안을 함께 기록합니다. 문서 커밋·푸시는 구현이나 외부 전송의 승인을 뜻하지 않습니다.

## 기본 방향

- 단일 모델 검수, 서로의 결과를 보지 않는 독립 검수, 특정 지적의 선택적 추가 검증을 구분합니다.
- 지적에는 위치, 원문·코드 근거, 이유, 영향, 수정 제안, 확신 및 검증 상태를 남깁니다.
- 원문 처리 범위를 추적하고, 요약·검색만 수행한 범위를 원문 전체 검수로 표시하지 않습니다.
- 실행 전 모델·전송 대상·예상 비용을 확인하고, 승인된 범위와 예산 안에서 실행합니다.
- Headless에서 숨은 입력 대기를 만들지 않고 승인·credential·예산 부족을 구조화된 상태로 반환합니다.
- HGLOS가 CLI를 호출하고 결과를 받아 검증 흐름에 활용할 수 있게 설계합니다.
- 보고서와 수정 제안을 분리하고 원본을 자동 수정하지 않습니다.
- 첫 버전은 Windows .NET 10 Headless CLI·텍스트/코드·HGLOS export·오프라인 검증판으로 범위를 확정했습니다. WPF 등 GUI와 실제 모델 연결은 후속입니다.

## 문서

| 문서 | 내용 |
|---|---|
| [요구사항](docs/REQUIREMENTS.md) | 검수 유형, 사용 흐름, 근거·처리 범위·비용·보안·원본 보호 |
| [Headless CLI 및 HGLOS 설계](docs/HEADLESS_DESIGN.md) | 엔진 책임, CLI 계약 초안, 상태·재개·승인, HGLOS 연결 |
| [결정 및 미결 사항](docs/DECISIONS.md) | 합의된 방향, 제안, 미확인, 단계별 범위와 검증 기준 |
| [제공자 조사 기록](docs/PROVIDERS.md) | 공식 자료 출처, 대화 당시 모델·가격 확인 기록과 한계 |
| [v0.1 구현 계획](docs/IMPLEMENTATION_PLAN_V0_1.md) | 확정 범위, Headless·HGLOS 우선 단계, API 없는 완료 기준 |
| [CLI·실행·예산 상세 계약](docs/CLI_AND_RUN_CONTRACT.md) | 명령·종료 코드·승인 신뢰 경계·상태 전이·월 예산·crash 복구 |
| [원문·근거·HGLOS 상세 계약](docs/SOURCE_EVIDENCE_AND_HGLOS.md) | 원문 좌표·diff·독립성·처리 범위·findings projection·보존 정책 |
| [독립 설계 검토 종결 기록](validation/design-review-20261104/DESIGN_REVIEW.md) | Claude Code 공식 사용법 적용, 독립 검토 원문·지적 반영·조건 해소·한계 |

## 다음 단계

설계 기준본과 단계별 완료 기준을 검토한 뒤 구현 착수 여부를 별도로 선택합니다. 구현과 API 키 연결은 명시적 요청 전에는 진행하지 않습니다. CLI 명령·필드·종료 코드는 설계 계약이며 현재 실행 가능한 기능이 아닙니다.
