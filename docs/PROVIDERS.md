# 제공자 조사 기록

기준: 2026-11-04 설계 대화에서 공개 공식 문서를 열어 확인한 기록. 계정·키·잔액·유료 추론 API는 조회하거나 호출하지 않았습니다.

아래 내용은 당시의 조사 스냅샷이며 실행 시점의 가격·지원·정책을 보장하지 않습니다. 구현 또는 모델 연결 전에 공식 자료와 실제 계정 조건을 재확인해야 합니다. 고정된 기본 모델이나 검수 품질 순위는 합의하지 않았습니다.

## 1. 모델·가격 후보

USD / 100만 토큰 기준의 표준 입력·출력 가격입니다. 캐시·Batch·지역 요금·세금·기타 서비스 비용을 제외합니다.

| 제공자 | 모델 ID | 입력 | 출력 | 당시 제안한 역할 |
|---|---|---:|---:|---|
| OpenAI | `gpt-6.1-sol` | $2 | $10 | 복합 요구사항·코드 주 검수 후보 |
| OpenAI | `gpt-6-luna` | $0.10 | $0.50 | 비용 우선의 제한된 기준 검수 후보 |
| Together AI | `zai-org/GLM-5.3-Flash` | $0.15 | $0.50 | 저비용 검수·추가 검증 후보 |
| Together AI | `Qwen/Qwen3.8-2.4T-A95B` | $2 | $6 | 다른 모델 계열의 심층 검수 후보 |
| Google AI | `gemini-3.8-flash` | $0.75 | $3.75 | 일반·독립 검수 후보 |
| Google AI | `gemini-4-argon` | $2 | $10 | 복잡한 추가 검증 후보 |

출처: [OpenAI 가격표](https://developers.openai.com/api/docs/pricing), [Together 가격표](https://www.together.ai/pricing), [GLM 모델](https://www.together.ai/models/glm-5-3-flash), [Qwen 모델](https://www.together.ai/models/qwen3-8-max), [Google 가격표](https://aistudio.google.com/docs/pricing).

당시 Google 가격표는 Flash가 2027-01-01부터 입력 $1.50 / 출력 $7.50, Argon이 2026-12-01부터 입력 $4 / 출력 $20로 변경될 예정이라고 명시했습니다. 가격 기록에는 확인일뿐 아니라 적용 기간·요금 조건을 포함해야 합니다.

한국어 소설, C#/WPF 코드, 기획서 검수의 실측 성능은 확인하지 않았습니다. 제공자의 모델 소개를 ReviewGate 품질 보증으로 취급하지 않습니다.

## 2. API 연결 방향

| 제공자 | 설계 후보 | 공식 자료에서 확인한 차이 |
|---|---|---|
| OpenAI | Responses API | 모델·옵션·저장·usage의 명시적 처리 필요 |
| Together AI | Chat Completions Adapter | OpenAI 호환이나 일부 필드·사용량·지원 범위는 다름 |
| Google AI | Interactions API | 신규 프로젝트 권장 인터페이스, `store=false`로 stateless 동작 선택 |

출처: [OpenAI 모델 안내](https://developers.openai.com/api/docs/models), [Together OpenAI 호환성](https://docs.together.ai/docs/inference/openai-compatibility), [Google Interactions API](https://aistudio.google.com/docs/interactions-overview).

Together 문서에는 Responses 지원도 기재되어 있습니다. Chat Completions는 최초 Adapter 후보일 뿐 영구 제한이 아닙니다. Google 문서상 기존 `generateContent`는 legacy이지만 지원 중입니다.

구조화 출력의 지원 여부·JSON Schema 부분집합·reasoning 옵션은 모델·API별로 확인해야 합니다. JSON 파싱 성공과 의미·근거의 정당성은 별개입니다.

출처: [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs), [Together Structured Outputs](https://docs.together.ai/docs/inference/chat/structured-outputs), [Gemini 모델 기능](https://aistudio.google.com/docs/models/gemini-3.8-flash).

## 3. 저장·학습·전달 정책의 차이

### OpenAI

당시 문서는 API 데이터를 기본적으로 학습에 사용하지 않는 것과 abuse monitoring·응답 상태 저장을 구분했습니다. `store=false`는 모든 로그·캐시·보관을 없애는 보증이 아니며 ZDR 적용 자격·설정·endpoint 예외가 있습니다.

출처: [OpenAI 데이터 관리](https://developers.openai.com/api/docs/guides/your-data).

### Together AI

당시 문서는 prompt 저장 기본 활성화, 조직의 Prompt storage 비활성화에 따른 ZDR, 별도의 학습 opt-in, 제3자에 전달하는 passthrough 모델을 구분했습니다. ZDR에서도 청구용 usage 등 metadata와 명시 업로드 파일의 취급은 별도입니다.

Together에서 직접 호스팅하는 외부 개발사 모델과 passthrough를 구분해야 합니다. 모델 개발사 이름만으로 요청이 해당 개발사에 전달된다고 추정하거나, Together를 경유한다는 이유로 항상 Together 내부에서만 처리된다고 추정하면 안 됩니다.

호환성 문서는 `store`를 수락하지만 무시한다고 명시했습니다. OpenAI용 필드를 그대로 보내 저장 정책이 통일되었다고 판단할 수 없습니다. 미국 처리 endpoint의 처리 위치 보장도 저장·로그·경로 전체의 보장과 다릅니다.

출처: [Together Privacy and Security](https://docs.together.ai/docs/privacy-and-security), [ZDR](https://docs.together.ai/docs/zero-data-retention), [호환성](https://docs.together.ai/docs/inference/openai-compatibility).

### Google AI

당시 약관은 무료·유료 서비스의 데이터 사용 조건을 구분했습니다. 유료 서비스의 제품 개선 미사용과 제한된 안전·정책 위반 방지 목적 기록을 구분하며, 유료 여부는 실제 billing 연결 조건과 대조해야 합니다. Interactions의 `store=false`도 모든 처리·보관의 부재를 뜻하지 않습니다.

출처: [Google 서비스 약관](https://aistudio.google.com/docs/terms), [Interactions 저장](https://aistudio.google.com/docs/interactions-overview).

## 4. 실행 전 추가 확인할 것

- 선택 모델·정확한 ID·버전·endpoint의 현재 지원과 계정 권한
- context·출력·reasoning 한도 및 구조화 출력의 실제 계약
- 현재 단가·가격 구간·지역 요금·청구 토큰 의미·캐시 조건
- 조직·프로젝트의 실제 저장·학습·passthrough·보존 설정
- 회사 자료 전송 권한과 승인된 최종 수신자
- rate limit·timeout·재시도와 청구 미확정 처리

이 문서의 링크는 공식 자료의 위치 기록입니다. API 호출 성공·계정 설정 확인·모델 성능 검증을 수행했다는 표시가 아닙니다.
