# 오프라인 wire 명세 fixture의 출처와 한계

조사일: 2026-11-04. 모델 서버를 호출하지 않고 공식 문서를 열었습니다.

| API profile | 공식 자료 | 검사하는 계약 |
|---|---|---|
| OpenAI Responses | https://developers.openai.com/api/docs/guides/structured-outputs | `text.format` JSON schema, output message/text, 별도 usage envelope |
| Together Chat Completions | https://docs.together.ai/docs/inference/openai-compatibility | messages/response_format, choices/finish_reason, cached/reasoning usage의 nested/flat 차이. store는 무시되는 옵션이므로 보관 통제로 표시하지 않음 |
| Google Interactions | https://aistudio.google.com/docs/interactions-overview 및 https://aistudio.google.com/docs/structured-output | stateless store=false, response_format의 type/mime_type/schema, steps의 model_output/content |

아래 excerpt 파일은 MAIN이 확인한 구조를 **재서술한 로컬 명세**입니다. manifest의 hash는 이 파일의 hash이며 공식 웹페이지 전체 hash나 독립적인 진위 보증이 아닙니다. ProviderTests의 mock은 request JSON을 검사하고 고정 응답을 돌려줍니다. 이 결과는 내부 wire 계약의 일관성만 증명합니다.

실서버의 모델별 schema subset·파라미터 수락·지원 API revision·token 의미·reasoning/cache 과금·오류별 usage는 미검증입니다. Google total과 input+output이 설명 없이 다르면 unknown으로 처리합니다. 모든 테스트 가격은 임의의 synthetic 값이며 시장 가격이 아닙니다. 실제 API header/auth/transport/SDK는 이 버전에 없습니다.
